using System.Threading;
using Microsoft.Extensions.Logging;
using MR.AspNetCore.Jobs.Client;

namespace MR.AspNetCore.Jobs.Server
{
	public class FireAndForgetJobProcessor : BackgroundJobProcessorBase
	{
		private CancellationTokenSource _cts;
		private CancellationTokenSource _linkedCts;

		public FireAndForgetJobProcessor(ILogger<FireAndForgetJobProcessor> logger)
			: base(logger)
		{
		}

		public override string ToString() => nameof(BackgroundJobProcessorBase);

		protected override IFetchedJob FetchNextJobCore(IStorageConnection connection)
		{
			return connection.FetchNextJob();
		}

		protected override void OnProcessEnter(ProcessingContext context)
		{
			_cts = new CancellationTokenSource();
			_linkedCts = CreateLinked(context);
			context.Pulsed += HandlePulse;
		}

		protected override void OnProcessExit(ProcessingContext context)
		{
			context.Pulsed -= HandlePulse;
			_linkedCts.Dispose();
			_cts.Dispose();
		}

		protected override CancellationToken GetTokenToWaitOn(ProcessingContext context)
		{
			return _linkedCts.Token;
		}

		private CancellationTokenSource CreateLinked(ProcessingContext context)
		{
			return CancellationTokenSource.CreateLinkedTokenSource(
				context.CancellationToken,
				_cts.Token);
		}

		private void HandlePulse(object sender, PulseKind kind)
		{
			switch (kind)
			{
				case PulseKind.BackgroundJobEnqueued:
					_cts.Cancel();
					break;
			}
		}
	}
}