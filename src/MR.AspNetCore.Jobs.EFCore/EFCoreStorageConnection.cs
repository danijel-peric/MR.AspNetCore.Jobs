using System;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MR.AspNetCore.Jobs.Models;
using MR.AspNetCore.Jobs.Server;

namespace MR.AspNetCore.Jobs
{
	public abstract class EFCoreStorageConnection
	{
		public EFCoreStorageConnection(
			EFCoreJobsDbContext context,
			EFCoreOptions options)
		{
			BaseContext = context;
			BaseOptions = options;
		}

		public EFCoreJobsDbContext BaseContext { get; }

		public EFCoreOptions BaseOptions { get; }

		public DbConnection GetDbConnection() => BaseContext.GetDbConnection();
	}

	public abstract class EFCoreStorageConnection<TContext, TOptions> : EFCoreStorageConnection, IStorageConnection
		where TContext : EFCoreJobsDbContext
		where TOptions : EFCoreOptions
	{
		public EFCoreStorageConnection(
			TContext context,
			TOptions options,
			IServiceProvider services)
			: base(context, options)
		{
			Context = context;
			Options = options;
			Services = services;
		}

		public EFCoreJobsDbContext Context { get; }

		public EFCoreOptions Options { get; }

		public IServiceProvider Services { get; }

		protected abstract bool UseTransactionFetchedJob { get; }

		public virtual Task StoreJobAsync(Job job)
		{
			if (job == null) throw new ArgumentNullException(nameof(job));

			job.Due = NormalizeDateTime(job.Due);

			Context.Add(job);
			return Context.SaveChangesAsync();
		}

		public virtual Task<Job> GetJobAsync(int id)
		{
			return Context.Jobs.FirstOrDefaultAsync(j => j.Id == id);
		}

		public virtual Task<IFetchedJob> FetchNextJobAsync()
		{
			var sql = CreateFetchNextJobQuery();
			return UseTransactionFetchedJob ?
				FetchNextDelayedJobUsingTransactionAsync(sql) :
				FetchNextDelayedJobUsingTimeoutAsync(sql);
		}

		protected abstract string CreateFetchNextJobQuery();

		public virtual async Task<Job> GetNextJobToBeEnqueuedAsync()
		{
			var sql = CreateGetNextJobToBeEnqueuedQuery();

			var connection = Context.GetDbConnection();
			var job = (await connection.QueryAsync<Job>(sql)).FirstOrDefault();

			if (job != null)
			{
				Context.Attach(job);
			}

			return job;
		}

		protected abstract string CreateGetNextJobToBeEnqueuedQuery();

		public virtual Task StoreCronJobAsync(CronJob job)
		{
			if (job == null) throw new ArgumentNullException(nameof(job));

			Context.Add(job);
			return Context.SaveChangesAsync();
		}

		public virtual Task AttachCronJobAsync(CronJob job)
		{
			if (job == null) throw new ArgumentNullException(nameof(job));

			Context.Attach(job);
			return Task.CompletedTask;
		}

		public virtual Task UpdateCronJobAsync(CronJob job)
		{
			if (job == null) throw new ArgumentNullException(nameof(job));

			return Context.SaveChangesAsync();
		}

		public virtual Task<CronJob[]> GetCronJobsAsync()
		{
			return Context.CronJobs.ToArrayAsync();
		}

		public virtual async Task RemoveCronJobAsync(string name)
		{
			var cronJob = await Context.CronJobs.FirstOrDefaultAsync(j => j.Name == name);
			if (cronJob != null)
			{
				Context.Remove(cronJob);
				await Context.SaveChangesAsync();
			}
		}

		public abstract IStorageTransaction CreateTransaction();

		public virtual void Dispose()
		{
		}

		private DateTime? NormalizeDateTime(DateTime? dateTime)
		{
			if (!dateTime.HasValue) return dateTime;
			if (dateTime == DateTime.MinValue)
			{
				return new DateTime(1754, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			}
			return dateTime;
		}

		private async Task<IFetchedJob> FetchNextDelayedJobUsingTransactionAsync(string sql)
		{
			FetchedJob fetchedJob = null;
			var connection = Context.GetDbConnection();
			var transaction = Context.Database.CurrentTransaction;
			transaction = transaction ?? await Context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

			try
			{
				fetchedJob =
					(await connection.QueryAsync<FetchedJob>(sql, null, transaction.GetDbTransaction()))
					.FirstOrDefault();
			}
			catch (SqlException)
			{
				transaction.Dispose();
				throw;
			}

			if (fetchedJob == null)
			{
				transaction.Rollback();
				transaction.Dispose();
				return null;
			}

			return new SqlTransactionFetchedJob(
				fetchedJob.JobId,
				connection,
				transaction);
		}

		private async Task<IFetchedJob> FetchNextDelayedJobUsingTimeoutAsync(string sql)
		{
			FetchedJob fetchedJob = null;
			var connection = Context.GetDbConnection();

			fetchedJob =
				(await connection.QueryAsync<FetchedJob>(sql))
				.FirstOrDefault();

			if (fetchedJob == null)
			{
				return null;
			}

			return new SqlTimeoutFetchedJob(
				Services,
				fetchedJob.Id,
				fetchedJob.JobId);
		}
	}
}
