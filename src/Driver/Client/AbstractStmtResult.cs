using System;

namespace TDengine.Driver.Client
{
    public abstract partial class AbstractStmt
    {
        public long Affected()
        {
            return _affectedRows;
        }

        public IRows Result()
        {
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
            if (!_executed)
            {
                throw new InvalidOperationException("Statement has not been executed yet.");
            }
        }
    }
}
