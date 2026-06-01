using System;

namespace TDengine.Driver.Client
{
    public abstract partial class AbstractStmt
    {
        public void AddBatch()
        {
            // check if the statement is prepared
            CheckPrepared();
            // check if the table name is set if required
            if (_needTableName && !IsTableNameSet)
            {
                throw new InvalidOperationException("Table name must be set before adding a batch.");
            }

            // check if tags are set if required
            if (NeedTags && !IsTagsSet)
            {
                throw new InvalidOperationException("Tags must be set before adding a batch.");
            }

            // check if columns are set
            if (!IsColSet)
            {
                throw new InvalidOperationException("Columns must be set before adding a batch.");
            }

            // check row count
            
            var rowCount = _currentTableInfo.Cols[0].Count;
            for (var i = 0; i < _currentTableInfo.Cols.Length; i++)
            {
                if (_currentTableInfo.Cols[i].Count == 0)
                {
                    throw new InvalidOperationException($"Column at index {i} has no rows to add.");
                }

                if (_currentTableInfo.Cols[i].Count != rowCount)
                {
                    throw new InvalidOperationException(
                        $"Column at index {i} has a different row count than the first column. Expected {rowCount}, but got {_currentTableInfo.Cols[i].Count}.");
                }
            }

            AddCurrentTableInfoToBatch();
            // reset the current table info

            _addBatched = true;
            CleanBatch();
        }

        private void AddCurrentTableInfoToBatch()
        {
            if (_isInsert && !_needTableName &&
                _tableInfos.TryGetValue(_currentTableInfo.TableName, out var existingTableInfo) &&
                !ReferenceEquals(existingTableInfo, _currentTableInfo))
            {
                AppendRows(existingTableInfo, _currentTableInfo);
                PutTableInfo(_currentTableInfo);
                return;
            }

            _tableInfos[_currentTableInfo.TableName] = _currentTableInfo;
        }

        private static void AppendRows(Stmt2TableData target, Stmt2TableData source)
        {
            if (target.Cols.Length != source.Cols.Length)
            {
                throw new InvalidOperationException("Current batch column count does not match the previous batch.");
            }

            for (var i = 0; i < source.Cols.Length; i++)
            {
                target.Cols[i].AddRange(source.Cols[i]);
            }
        }
    }
}
