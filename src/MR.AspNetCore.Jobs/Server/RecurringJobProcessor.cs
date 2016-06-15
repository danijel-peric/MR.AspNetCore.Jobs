using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MR.AspNetCore.Jobs.Models;

namespace MR.AspNetCore.Jobs.Server
{
	public class RecurringJobProcessor : IProcessor
	{
		private ComputedCronJobsElector _cronJobsElector = new ComputedCronJobsElector();
		private ILogger<RecurringJobProcessor> _logger;

		public RecurringJobProcessor(ILogger<RecurringJobProcessor> logger)
		{
			_logger = logger;
		}

		public override string ToString() => nameof(RecurringJobProcessor);

		public void Process(ProcessingContext context)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));

			var storage = context.Storage;
			CronJob[] jobs = null;

			using (var connection = storage.GetConnection())
			{
				jobs = connection.GetCronJobs();
			}
			if (!jobs.Any())
			{
				// Stop the RecurringJobProcessor
				_logger.LogInformation("Couldn't find any cron jobs to schedule, cancelling processing.");
				throw new ProcessingCanceledException();
			}
			_logger.LogInformation($"Found {jobs.Length} cron job(s) to schedule.");

			var computed = Compute(jobs);
			while (!context.IsStopping)
			{
				var now = DateTime.UtcNow;
				var nextJob = ElectNextJob(computed);
				var due = nextJob.Next;
				var timeSpan = due - now;
				if (timeSpan.TotalSeconds > 0)
				{
					context.Wait(timeSpan);
				}
				if (context.IsStopping)
				{
					break;
				}

				_logger.LogInformation(
					"Preparing cron job: \"{jobName}\" for execution.", new { jobName = nextJob.Job.Name });
				using (var scopedContext = context.CreateScope())
				{
					var factory = scopedContext.Provider.GetService<IJobFactory>();
					var job = scopedContext.Provider.GetService(nextJob.JobType) as IJob;
					var sw = Stopwatch.StartNew();
					job.ExecuteAsync().GetAwaiter().GetResult();
					sw.Stop();
					_logger.LogInformation(
						"Cron job: \"{jobName}\" executed succesfully. Took: {seconds} secs.",
						new { jobName = nextJob.Job.Name, seconds = sw.Elapsed.TotalSeconds });

					using (var connection = storage.GetConnection())
					{
						now = DateTime.UtcNow;
						nextJob.Update(now);
						connection.UpdateCronJob(nextJob.Job);
					}
				}
			}
		}

		private ComputedCronJob[] Compute(IEnumerable<CronJob> jobs)
		{
			return jobs.Select(CreateComputedCronJob).ToArray();
		}

		private ComputedCronJob CreateComputedCronJob(CronJob job)
		{
			return new ComputedCronJob(job);
		}

		private ComputedCronJob ElectNextJob(IEnumerable<ComputedCronJob> jobs)
		{
			return _cronJobsElector.Elect(jobs);
		}
	}
}
