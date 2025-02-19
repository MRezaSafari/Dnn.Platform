﻿#region Copyright
// 
// DotNetNuke® - http://www.dnnsoftware.com
// Copyright (c) 2002-2017
// by DotNetNuke Corporation
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated 
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and 
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or substantial portions 
// of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED 
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF 
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Dnn.ExportImport.Components.Common;
using Dnn.ExportImport.Components.Controllers;
using Dnn.ExportImport.Components.Dto.Jobs;
using Dnn.ExportImport.Components.Engines;
using Dnn.ExportImport.Components.Models;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Exceptions;
using DotNetNuke.Services.Localization;
using DotNetNuke.Services.Scheduling;
using PlatformDataProvider = DotNetNuke.Data.DataProvider;

namespace Dnn.ExportImport.Components.Scheduler
{
    /// <summary>
    /// Implements a SchedulerClient for the Exporting/Importing of site items.
    /// </summary>
    public class ExportImportScheduler : SchedulerClient
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ExportImportScheduler));

        public ExportImportScheduler(ScheduleHistoryItem objScheduleHistoryItem)
        {
            ScheduleHistoryItem = objScheduleHistoryItem;
        }

        public override void DoWork()
        {
            try
            {
                //TODO: do some clean-up for very old import/export jobs/logs

                var job = EntitiesController.Instance.GetFirstActiveJob();
                if (job == null)
                {
                    ScheduleHistoryItem.Succeeded = true;
                    ScheduleHistoryItem.AddLogNote("<br/>No Site Export/Import jobs queued for processing.");
                }
                else if (job.IsCancelled)
                {
                    job.JobStatus = JobStatus.Cancelled;
                    EntitiesController.Instance.UpdateJobStatus(job);
                    ScheduleHistoryItem.Succeeded = true;
                    ScheduleHistoryItem.AddLogNote("<br/>Site Export/Import jobs was previously cancelled.");
                }
                else
                {
                    job.JobStatus = JobStatus.InProgress;
                    EntitiesController.Instance.UpdateJobStatus(job);
                    ExportImportResult result;
                    var engine = new ExportImportEngine();

                    switch (job.JobType)
                    {
                        case JobType.Export:
                            result = engine.Export(job, ScheduleHistoryItem);
                            EntitiesController.Instance.UpdateJobStatus(job);
                            break;
                        case JobType.Import:
                            result = engine.Import(job, ScheduleHistoryItem);
                            EntitiesController.Instance.UpdateJobStatus(job);
                            if (job.JobStatus == JobStatus.Successful || job.JobStatus == JobStatus.Cancelled)
                            {
                                // clear everything to be sure imported items take effect
                                DataCache.ClearCache();
                            }
                            break;
                        default:
                            throw new Exception("Unknown job type: " + job.JobType);
                    }


                    if (result != null)
                    {
                        ScheduleHistoryItem.Succeeded = true;
                        var sb = new StringBuilder();
                        var jobType = Localization.GetString("JobType_" + job.JobType, Constants.SharedResources);
                        var jobStatus = Localization.GetString("JobStatus_" + job.JobStatus, Constants.SharedResources);
                        sb.AppendFormat("<br/><b>{0} {1}</b>", jobType, jobStatus);
                        var summary = result.Summary;
                        if (summary.Count > 0)
                        {
                            sb.Append("<br/><b>Summary:</b><ul>");
                            foreach (var entry in summary)
                            {
                                sb.Append($"<li>{entry.Name}: {entry.Value}</li>");
                            }
                            sb.Append("</ul>");
                        }

                        ScheduleHistoryItem.AddLogNote(sb.ToString());
                        AddLogsToDatabase(job.JobId, result.CompleteLog);
                    }

                    Logger.Trace("Site Export/Import: Job Finished");
                }
                //SetLastSuccessfulIndexingDateTime(ScheduleHistoryItem.ScheduleID, ScheduleHistoryItem.StartDate);
            }
            catch (Exception ex)
            {
                ScheduleHistoryItem.Succeeded = false;
                ScheduleHistoryItem.AddLogNote("<br/>Export/Import EXCEPTION: " + ex.Message);
                Errored(ref ex);
                if (ScheduleHistoryItem.ScheduleSource != ScheduleSource.STARTED_FROM_BEGIN_REQUEST)
                {
                    Exceptions.LogException(ex);
                }
            }
        }

        private static void AddLogsToDatabase(int jobId, ICollection<LogItem> completeLog)
        {
            if (completeLog == null || completeLog.Count == 0) return;

            using (var table = new DataTable("ExportImportJobLogs"))
            {
                // must create the columns from scratch with each iteration
                table.Columns.AddRange(DatasetColumns.Select(
                    column => new DataColumn(column.Item1, column.Item2)).ToArray());

                // batch specific amount of record each time
                const int batchSize = 500;
                var toSkip = 0;
                while (toSkip < completeLog.Count)
                {
                    foreach (var item in completeLog.Skip(toSkip).Take(batchSize))
                    {
                        var row = table.NewRow();
                        row["JobId"] = jobId;
                        row["Name"] = item.Name.TrimToLength(Constants.LogColumnLength);
                        row["Value"] = item.Value.TrimToLength(Constants.LogColumnLength);
                        row["IsSummary"] = item.IsSummary;
                        row["CreatedOnDate"] = item.CreatedOnDate;
                        table.Rows.Add(row);
                    }

                    toSkip += batchSize;
                    PlatformDataProvider.Instance().BulkInsert("ExportImportJobLogs_AddBulk", "@DataTable", table);
                }
            }
        }

        private static readonly Tuple<string, Type>[] DatasetColumns =
        {
            new Tuple<string,Type>("JobId", typeof(int)),
            new Tuple<string,Type>("Name" , typeof(string)),
            new Tuple<string,Type>("Value", typeof(string)),
            new Tuple<string,Type>("IsSummary", typeof(bool)),
            new Tuple<string,Type>("CreatedOnDate", typeof(DateTime)),
        };
    }
}