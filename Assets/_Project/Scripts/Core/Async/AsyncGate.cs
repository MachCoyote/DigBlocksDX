using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Core.Async
{
    //serializes asynchronous operations so lifecycle transitions cannot interleave
    public sealed class AsyncGate
    {
        private UniTask tail = UniTask.CompletedTask;

        public async UniTask<Releaser> EnterAsync(CancellationToken cancellationToken)
        {
            var completion = new UniTaskCompletionSource();
            UniTask predecessor = tail;
            tail = completion.Task;

            await predecessor;

            if (cancellationToken.IsCancellationRequested)
            {
                //hand the gate straight to the next waiter instead of stalling the queue
                completion.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new Releaser(completion);
        }

        public readonly struct Releaser : IDisposable
        {
            private readonly UniTaskCompletionSource completion;

            internal Releaser(UniTaskCompletionSource completion)
            {
                this.completion = completion;
            }

            public void Dispose()
            {
                completion?.TrySetResult();
            }
        }
    }
}
