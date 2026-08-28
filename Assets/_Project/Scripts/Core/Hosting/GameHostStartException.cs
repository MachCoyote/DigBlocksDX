using System;
using System.Collections.Generic;

namespace DigBlocks.Core.Hosting
{
    public sealed class GameHostStartException : Exception
    {
        public GameHostStartException(
            string message,
            Exception startupException,
            IReadOnlyList<Exception> rollbackErrors)
            : base(message, startupException)
        {
            if (rollbackErrors == null)
            {
                throw new ArgumentNullException(nameof(rollbackErrors));
            }

            var errors = new Exception[rollbackErrors.Count];
            for (int index = 0; index < rollbackErrors.Count; index++)
            {
                errors[index] = rollbackErrors[index];
            }

            RollbackErrors = Array.AsReadOnly(errors);
        }

        public IReadOnlyList<Exception> RollbackErrors { get; }
    }
}
