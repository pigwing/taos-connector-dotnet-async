using System;

namespace TDengine.Driver.Client
{
    public abstract partial class AbstractStmt
    {
        public virtual void SetTableName(string tableName)
        {
            CheckPrepared();
            if (_needTableName)
            {
                if (IsTableNameSet)
                {
                    throw new InvalidOperationException(
                        "Table name has already been set for current batch");
                }

                if (string.IsNullOrEmpty(tableName))
                {
                    throw new ArgumentException("Table name cannot be null or empty");
                }

                if (tableName.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException("Table name cannot contain a null character", nameof(tableName));
                }

                _currentTableInfo.TableName = tableName;

                IsTableNameSet = true;
            }
            else
            {
                throw new InvalidOperationException(
                    "Table name is not required for this statement or not supported in this context.");
            }
        }

        public virtual void SetTags(object[] tags)
        {
            CheckPrepared();
            CheckTableNameSet();
            if (tags == null || tags.Length == 0)
            {
                throw new ArgumentException("Tags cannot be null or empty");
            }

            if (_tagFields == null || _tagFields.Length == 0 || !_isInsert)
            {
                throw new InvalidOperationException("This statement does not need tags.");
            }

            if (tags.Length != _tagFields.Length)
            {
                throw new ArgumentException(
                    $"Expected {_tagFields.Length} tags, but got {tags.Length}");
            }

            CheckRowValue(tags, _tagFields);
            var localTags = new object[tags.Length];
            for (var i = 0; i < tags.Length; i++)
            {
                localTags[i] = SnapshotBindValue(tags[i]);
            }

            if (IsTagsSet)
            {
                if (!TagsEqual(_currentTableInfo.Tags, localTags))
                {
                    throw new InvalidOperationException("Tags have already been set with different values for the current batch.");
                }

                return;
            }

            _currentTableInfo.Tags = localTags;
            IsTagsSet = true;
        }
    }
}
