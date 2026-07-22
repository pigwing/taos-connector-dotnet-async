using System.Collections.Generic;

namespace TDengine.Driver.Client
{
    class Stmt2TableData
    {
        public string TableName;
        public List<object>[] Cols;
        public object[] Tags;
        
        public Stmt2TableData(List<object>[] cols)
        {
            TableName = string.Empty;
            Cols = cols;
        }

        public bool IsColSet => Cols != null && Cols.Length > 0 && Cols[0] != null && Cols[0].Count > 0;
        public int Rows => IsColSet ? Cols[0].Count : 0;
    }
    
    public abstract partial class AbstractStmt : IStmt
    {
        private readonly int _binaryHeaderLength;
        private string _sql = string.Empty;
        private bool _isInsert;
        private int _fieldsCount;
        private TaosFieldAll[] _fields;
        private TaosFieldE[] _tagFields;
        private TaosFieldE[] _colFields;

        // private IFieldBuilder[] _colBuilders;
        // private IFieldBuilder[] _tagBuilders;
        private bool _needTableName;
        
        private readonly Dictionary<string, Stmt2TableData> _tableInfos = new Dictionary<string, Stmt2TableData>();
        private Stmt2TableData _currentTableInfo;
        private bool _isTableNameSet;
        private bool _isTagsSet;
        private bool _isColSet;
        private bool _addBatched;
        private bool _executed;
        private int _affectedRows;
        private bool _schemaChanged;
        private TaosFieldE[] _queryFields;
        
        private readonly Queue<List<object>> _objectListQueue = new Queue<List<object>>();
        private readonly Queue<Stmt2TableData> _tableInfoQueue = new Queue<Stmt2TableData>();
        private const int MaxCachedObjectLists = 256;
        private const int MaxCachedTableInfos = 64;
        private const int MaxCachedObjectListCapacity = 4096;
        private const int MaxCachedObjectListTotalCapacity = 65536;
        private int _cachedObjectListCapacity;
        
        // after prepare or add batch, get a new table info
        private Stmt2TableData GetStmt2TableData()
        {
            var colLength = _isInsert ? _colFields.Length : _fieldsCount;

            var info =
                // get table info from cache
                _tableInfoQueue.Count > 0 ? _tableInfoQueue.Dequeue() :
                // create new table info
                new Stmt2TableData(new List<object>[colLength]);

            // ensure the array length is correct, if not enough, recreate it
            if (info.Cols.Length != colLength)
            {
                info.Cols = new List<object>[colLength];
            }
    
            // fill the lists
            for (var i = 0; i < info.Cols.Length; i++)
            {
                // get from cache or create new
                info.Cols[i] = GetObjectList();
            }
            return info;
        }

        private List<object> GetObjectList()
        {
            if (_objectListQueue.Count == 0)
            {
                return new List<object>();
            }

            var list = _objectListQueue.Dequeue();
            _cachedObjectListCapacity -= list.Capacity;
            return list;
        }
        
        // after execute, put table info to cache
        private void PutTableInfo(Stmt2TableData info)
        {
            if (info == null) return;
            // clear all column lists and return to cache
            for (var i = 0; i < info.Cols.Length; i++)
            {
                var list = info.Cols[i];
                if (list == null)
                {
                    continue;
                }

                list.Clear();
                if (list.Capacity <= MaxCachedObjectListCapacity &&
                    _objectListQueue.Count < MaxCachedObjectLists &&
                    list.Capacity <= MaxCachedObjectListTotalCapacity - _cachedObjectListCapacity)
                {
                    _objectListQueue.Enqueue(list);
                    _cachedObjectListCapacity += list.Capacity;
                }
                info.Cols[i] = null;
            }
            info.Tags = null;
            info.TableName = string.Empty;
            // return to cache
            if (_tableInfoQueue.Count < MaxCachedTableInfos)
            {
                _tableInfoQueue.Enqueue(info);
            }
        }
        
        protected AbstractStmt(int binaryHeaderLength = 0)
        {
            _binaryHeaderLength = binaryHeaderLength;
        }
        
        // before prepare or prepare failed, clean all cache
        private void CleanCache()
        {
            _sql = string.Empty;
            _isInsert = false;
            _fieldsCount = 0;
            _fields = null;
            _tagFields = null;
            _colFields = null;
            _needTableName = false;
            _tableInfos.Clear();
            _isTableNameSet = false;
            _isTagsSet = false;
            _isColSet = false;
            _addBatched = false;
            _executed = false;
            _affectedRows = 0;
            _schemaChanged = false;
            _queryFields = null;
            _currentTableInfo = null;
            // clean cached object lists and table info queue
            _tableInfoQueue.Clear();
            _objectListQueue.Clear();
            _cachedObjectListCapacity = 0;
        }

        // after add batch, clean current batch info
        private void CleanBatch()
        {
            _isTableNameSet = false;
            _isTagsSet = false;
            _isColSet = false;
            _currentTableInfo = GetStmt2TableData();
        }
        
        // after execute, put all table info to cache
        private void CleanExec()
        {
            CleanExec(true);
        }

        private void CleanExec(bool executed)
        {
            if (!_isInsert)
            {
                _queryFields = null;
            }

            _addBatched = false;
            _executed = executed;
            foreach (var tableInfo in _tableInfos.Values)
            {
                // return table info to cache
                PutTableInfo(tableInfo);
            }
            _tableInfos.Clear();
        }

        protected void ResetExecutionState()
        {
            CleanExec(false);
        }

        protected void ClearStatementCache()
        {
            CleanCache();
        }

        protected virtual void ThrowIfDisposed()
        {
        }

        public abstract void Dispose();
    }
}
