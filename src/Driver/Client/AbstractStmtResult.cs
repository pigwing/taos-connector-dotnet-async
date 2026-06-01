using System;

namespace TDengine.Driver.Client
{
    public abstract partial class AbstractStmt
    {
        public long Affected()
        {
            ThrowIfDisposed();
            return _affectedRows;
        }

        public IRows Result()
        {
            ThrowIfDisposed();
            CheckExecuted();

            if (_isInsert)
            {
                return InsertResultInternal(_affectedRows);
            }

            return QueryResultInternal();
        }

        protected abstract IRows QueryResultInternal();
        protected abstract IRows InsertResultInternal(int affectedRows);

        protected void CheckExecuted()
        {
            ThrowIfDisposed();
            if (!_executed)
            {
                throw new InvalidOperationException("Statement has not been executed yet.");
            }
        }
    }
}
