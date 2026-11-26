using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DbConvert
{
    /// <summary>按批把数据从源表搬到目标表。</summary>
    public class DataPump
    {
        private readonly SchemaSession _src;
        private readonly SchemaSession _dst;
        private readonly OptionsConfig _opt;
        private readonly Action<string> _log;
        private readonly ValueConverter _conv;
        private readonly List<string> _warnings;

        public DataPump(SchemaSession src, SchemaSession dst, OptionsConfig opt, Action<string> log, List<string> warnings)
        {
            _src = src;
            _dst = dst;
            _opt = opt ?? new OptionsConfig();
            _log = log ?? (s => { });
            _warnings = warnings ?? new List<string>();
            _conv = new ValueConverter(dst.Dialect, _opt, _log);
        }

        public long CopyTable(TableDef t, string where, Action<long> progress)
        {
            var columns = t.DataColumns();
            if (columns.Count == 0) throw new InvalidOperationException("表 " + t.QualifiedName + " 没有可搬运的列");

            string srcRef = _src.Dialect.Qualify(t.Schema, t.Name);
            string dstRef = _dst.Dialect.Qualify(t.TargetSchema, t.TargetName);

            var selectCols = new List<string>();
            foreach (ColumnDef c in columns) selectCols.Add(_src.Dialect.Quote(c.Name));

            string orderBy = "";
            string resumeCondition = "";
            object resumeKeyValue = null;

            if (_opt.Resume)
            {
                string statePath = ResolveStatePath();
                TableState state = StateStore.Load(statePath).Get(t.QualifiedName);
                if (state != null && state.RowsCopied > 0)
                {
                    IndexDef pk = t.PrimaryKey;
                    if (pk != null && pk.Columns.Count == 1)
                    {
                        ColumnDef pkCol = t.FindColumn(pk.Columns[0].Name);
                        if (pkCol != null)
                        {
                            resumeKeyValue = state.LastKeyValue == null ? null : DeserializeKey(state.LastKeyValue, pkCol);
                            string pkRef = _src.Dialect.Quote(pkCol.Name);
                            orderBy = " ORDER BY " + pkRef;
                            string literal = FormatKeyLiteral(resumeKeyValue, pkCol, _src.Dialect);
                            if (literal != null)
                            {
                                resumeCondition = " WHERE " + pkRef + " > " + literal;
                                int trimmed = TrimTargetBeyond(t, pkCol, literal, dstRef);
                                _log("从断点续传：" + t.QualifiedName + " 已有 " + state.RowsCopied + " 行，跳过主键 <= " + literal +
                                     (trimmed > 0 ? "，先清理目标表中断点之后未记录的多余 " + trimmed + " 行" : ""));
                            }
                        }
                    }
                    else
                    {
                        _warnings.Add("表 " + t.QualifiedName + " 无单列主键，忽略断点续传");
                    }
                }
            }

            if (string.IsNullOrEmpty(orderBy))
            {
                IndexDef pk = t.PrimaryKey;
                if (pk != null && pk.Columns.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (IndexColumn ic in pk.Columns) parts.Add(_src.Dialect.Quote(ic.Name));
                    orderBy = " ORDER BY " + string.Join(", ", parts);
                }
            }

            string whereClause = "";
            if (!string.IsNullOrWhiteSpace(where))
            {
                whereClause = " WHERE (" + where + ")";
            }
            if (!string.IsNullOrEmpty(resumeCondition))
            {
                whereClause = string.IsNullOrEmpty(where) ? resumeCondition : (" WHERE (" + where + ") AND " + resumeCondition.Substring(" WHERE ".Length));
            }

            string selectSql = "SELECT " + string.Join(", ", selectCols) + " FROM " + srcRef + whereClause + orderBy;

            var writer = new BulkWriter(_dst.Connection, _dst.Dialect, dstRef, columns, _opt, _log);
            long rows = 0;
            long skipped = 0;
            long totalRows = 0;
            long lastCheckpoint = 0;
            StateStore store = null;
            string stateFilePath = null;
            if (_opt.Resume)
            {
                stateFilePath = ResolveStatePath();
                store = StateStore.Load(stateFilePath);
                var existing = store.Get(t.QualifiedName);
                totalRows = existing?.RowsCopied ?? 0;
                rows = totalRows;
                lastCheckpoint = totalRows;
            }

            DateTime started = DateTime.Now;
            DateTime lastLog = DateTime.Now;

            using (var cmd = _src.Connection.CreateCommand())
            {
                cmd.CommandText = selectSql;
                if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;
                cmd.CommandTimeout = cmd.CommandTimeout <= 0 ? 0 : cmd.CommandTimeout;
                using (var rd = cmd.ExecuteReader(CommandBehavior.Default))
                {
                    object[] raw = new object[columns.Count];
                    object[] converted = new object[columns.Count];
                    while (rd.Read())
                    {
                        rd.GetValues(raw);
                        bool skip = false;
                        for (int i = 0; i < columns.Count; i++)
                        {
                            ColumnDef c = columns[i];
                            object v = Unwrap(raw[i]);
                            try
                            {
                                converted[i] = _conv.ConvertValue(v, c, c);
                            }
                            catch (SkipRowException ex)
                            {
                                _warnings.Add("表 " + t.QualifiedName + " 跳过一行：" + ex.Message);
                                skip = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (_opt.ContinueOnError)
                                {
                                    _warnings.Add("表 " + t.QualifiedName + " 跳过一行（列 " + c.Name + "）：" + ex.Message);
                                    skip = true;
                                    break;
                                }
                                throw new InvalidOperationException("表 " + t.QualifiedName + " 第 " + (rows + 1) +
                                                                    " 行列 " + c.Name + " 转换失败：" + ex.Message, ex);
                            }
                        }
                        if (skip)
                        {
                            skipped++;
                            continue;
                        }

                        // 注意：converted 数组每行都会复用，写入器里必须保存快照
                        object[] snapshot = new object[columns.Count];
                        Array.Copy(converted, snapshot, columns.Count);
                        writer.Add(snapshot);
                        rows++;

                        if ((rows % 2000) == 0 || (DateTime.Now - lastLog).TotalSeconds >= 3)
                        {
                            lastLog = DateTime.Now;
                            double rps = rows / Math.Max(0.001, (DateTime.Now - started).TotalSeconds);
                            _log("  " + t.QualifiedName + " 已写入 " + rows + " 行，" + rps.ToString("0") + " 行/秒");
                        }
                        progress?.Invoke(rows + skipped);

                        if (_opt.Resume && store != null)
                        {
                            IndexDef pk = t.PrimaryKey;
                            if (pk != null && pk.Columns.Count == 1)
                            {
                                var st = store.GetOrCreate(t.QualifiedName);
                                st.RowsCopied = rows;
                                st.LastKeyValue = SerializeKey(converted[FindColumnIndex(columns, pk.Columns[0].Name)], t.FindColumn(pk.Columns[0].Name));
                                // 必须在缓冲已落库时记录断点，否则续传会跳过尚未写入的行
                                if (writer.PendingCount == 0 && rows - lastCheckpoint >= 5000)
                                {
                                    store.Save(stateFilePath);
                                    lastCheckpoint = rows;
                                }
                            }
                        }
                    }
                }
            }

            writer.Complete();
            if (store != null && stateFilePath != null) store.Save(stateFilePath);

            long readMs = (long)(DateTime.Now - started).TotalMilliseconds - writer.WriteMilliseconds;
            _log("表 " + t.QualifiedName + " 完成：写入 " + (rows - totalRows) + " 行" + (skipped > 0 ? "，跳过 " + skipped + " 行" : "") +
                 "（读+转换 " + readMs + " ms，写库 " + writer.WriteMilliseconds + " ms）");
            return rows - totalRows;
        }

        private static int FindColumnIndex(List<ColumnDef> columns, string name)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>断点之后、但已写入目标表的行会在续传时重复，先删掉它们。</summary>
        private int TrimTargetBeyond(TableDef t, ColumnDef pkCol, string literal, string dstRef)
        {
            try
            {
                using (var cmd = _dst.Connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM " + dstRef + " WHERE " + _dst.Dialect.Quote(pkCol.TargetName) + " > " + literal;
                    if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;
                    return cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _warnings.Add("续传清理目标表失败（可能造成主键冲突）：" + ex.Message);
                return 0;
            }
        }

        private string ResolveStatePath()
        {
            if (!string.IsNullOrWhiteSpace(_opt.StateFile)) return _opt.StateFile;
            string dir = ReportWriter.ResolveOutDir(_opt);
            return Path.Combine(dir, "migration-state.json");
        }

        private static string SerializeKey(object value, ColumnDef col)
        {
            if (value == null || value is DBNull) return null;
            return ValueConverter.ToStringValue(value);
        }

        private static object DeserializeKey(string text, ColumnDef col)
        {
            if (string.IsNullOrEmpty(text)) return null;
            switch (col.Kind)
            {
                case ColumnKind.Int:
                case ColumnKind.BigInt:
                case ColumnKind.SmallInt:
                case ColumnKind.TinyInt:
                    return long.Parse(text, CultureInfo.InvariantCulture);
                case ColumnKind.Date:
                case ColumnKind.DateTime:
                case ColumnKind.TimestampTz:
                    return DateTime.Parse(text, CultureInfo.InvariantCulture);
                case ColumnKind.Decimal:
                case ColumnKind.Money:
                    return decimal.Parse(text, CultureInfo.InvariantCulture);
                default:
                    return text;
            }
        }

        private static string FormatKeyLiteral(object value, ColumnDef col, Dialect d)
        {
            if (value == null) return null;
            switch (col.Kind)
            {
                case ColumnKind.Int:
                case ColumnKind.BigInt:
                case ColumnKind.SmallInt:
                case ColumnKind.TinyInt:
                    return Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture);
                case ColumnKind.Decimal:
                case ColumnKind.Money:
                    return Convert.ToDecimal(value).ToString(CultureInfo.InvariantCulture);
                case ColumnKind.Date:
                    return SqlUtil.Literal(((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                case ColumnKind.DateTime:
                case ColumnKind.TimestampTz:
                    return SqlUtil.Literal(((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                default:
                    return SqlUtil.Literal(value.ToString());
            }
        }

        /// <summary>把 Oracle/DM 的 Clob/Blob 对象、Stream、char[] 等展开成普通 CLR 值。</summary>
        public static object Unwrap(object value)
        {
            if (value == null || value is DBNull) return null;
            if (value is char[]) return new string((char[])value);
            if (value is byte[]) return value;
            if (value is string) return value;

            string typeName = value.GetType().Name;
            if (typeName.Contains("Clob") || typeName.Contains("Blob") || typeName.IndexOf("Lob", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var pi = value.GetType().GetProperty("Value");
                if (pi != null)
                {
                    try { return pi.GetValue(value); } catch { }
                }
            }
            if (value is Stream)
            {
                using (var ms = new MemoryStream())
                {
                    ((Stream)value).CopyTo(ms);
                    return ms.ToArray();
                }
            }
            if (value is TextReader)
            {
                return ((TextReader)value).ReadToEnd();
            }
            return value;
        }
    }

    /// <summary>批量写入器：MySQL/PG/SQLServer/SQLite 走多值 INSERT，Oracle/达梦逐行绑定。</summary>
    public class BulkWriter
    {
        private readonly DbConnection _conn;
        private readonly Dialect _d;
        private readonly string _tableRef;
        private readonly List<ColumnDef> _columns;
        private readonly OptionsConfig _opt;
        private readonly Action<string> _log;
        private readonly List<object[]> _pending = new List<object[]>();
        private readonly int _rowsPerStatement;
        private readonly bool _multiRow;
        private long _written;
        private long _writeMs;

        /// <summary>累计写库耗时（毫秒），用于诊断性能。</summary>
        public long WriteMilliseconds { get { return _writeMs; } }

        /// <summary>尚未落库的行数；断点状态只允许在缓冲清空后保存。</summary>
        public int PendingCount { get { return _pending.Count; } }

        public BulkWriter(DbConnection conn, Dialect d, string tableRef, List<ColumnDef> columns, OptionsConfig opt, Action<string> log)
        {
            _conn = conn;
            _d = d;
            _tableRef = tableRef;
            _columns = columns;
            _opt = opt;
            _log = log;
            _multiRow = d.SupportsMultiRowInsert && columns.Count > 0 && d.Family != DialectFamily.Oracle;
            int batch = Math.Max(1, opt.BatchSize);
            int maxParams = d.MaxParameters;
            int byParams = columns.Count > 0 ? Math.Max(1, (maxParams - 4) / columns.Count) : 1;
            _rowsPerStatement = _multiRow ? Math.Min(batch, byParams) : batch;
            if (_rowsPerStatement > batch) _rowsPerStatement = batch;
        }

        public void Add(object[] row)
        {
            _pending.Add(row);
            if (_pending.Count >= _rowsPerStatement) Flush();
        }

        public void Complete()
        {
            Flush();
        }

        private string BuildSql(int rowCount)
        {
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(_tableRef).Append(" (");
            for (int i = 0; i < _columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_d.Quote(_columns[i].TargetName));
            }
            sb.Append(") VALUES ");
            int paramIndex = 0;
            for (int r = 0; r < rowCount; r++)
            {
                if (r > 0) sb.Append(", ");
                sb.Append('(');
                for (int i = 0; i < _columns.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_d.ParameterPrefix).Append('p').Append(paramIndex++);
                }
                sb.Append(')');
            }
            return sb.ToString();
        }

        private void Flush()
        {
            if (_pending.Count == 0) return;
            int rowCount = _pending.Count;
            var sw = Stopwatch.StartNew();
            try
            {
                FlushCore();
            }
            finally
            {
                _writeMs += sw.ElapsedMilliseconds;
            }
        }

        private void FlushCore()
        {
            int rowCount = _pending.Count;

            if (_multiRow)
            {
                string sql = BuildSql(rowCount);
                try
                {
                    ExecuteBatch(sql, rowCount, null);
                }
                catch (Exception ex)
                {
                    if (!_opt.ContinueOnError) throw;
                    _log("warn: 批量写入失败，改为逐行写入定位问题行：" + ex.Message);
                    foreach (object[] row in _pending)
                    {
                        try { ExecuteBatch(BuildSql(1), 1, row); }
                        catch (Exception inner)
                        {
                            _log("warn: 跳过一行：" + inner.Message);
                        }
                    }
                }
            }
            else
            {
                try
                {
                    ExecuteRowBatch(_pending, BuildSql(1));
                }
                catch (Exception ex)
                {
                    if (!_opt.ContinueOnError) throw;
                    _log("warn: 批量写入失败，改为逐行写入定位问题行：" + ex.Message);
                    foreach (object[] row in _pending)
                    {
                        var one = new List<object[]> { row };
                        try { ExecuteRowBatch(one, BuildSql(1)); }
                        catch (Exception inner) { _log("warn: 跳过一行：" + inner.Message); }
                    }
                }
            }

            _written += rowCount;
            _pending.Clear();
        }

        private void ExecuteRowBatch(List<object[]> rows, string sql)
        {
            using (var tx = _conn.BeginTransaction())
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;

                for (int i = 0; i < _columns.Count; i++)
                {
                    var prm = cmd.CreateParameter();
                    prm.ParameterName = "p" + i;
                    prm.Value = DBNull.Value;
                    cmd.Parameters.Add(prm);
                }

                try
                {
                    foreach (object[] row in rows)
                    {
                        for (int i = 0; i < _columns.Count; i++)
                        {
                            cmd.Parameters[i].Value = row[i] ?? DBNull.Value;
                        }
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
        }

        private void ExecuteBatch(string sql, int rowCount, object[] singleRow)
        {
            using (var tx = _conn.BeginTransaction())
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;

                for (int p = 0; p < rowCount * _columns.Count; p++)
                {
                    var prm = cmd.CreateParameter();
                    prm.ParameterName = "p" + p;
                    prm.Value = DBNull.Value;
                    cmd.Parameters.Add(prm);
                }

                if (singleRow != null)
                {
                    for (int i = 0; i < _columns.Count; i++)
                    {
                        cmd.Parameters[i].Value = singleRow[i] ?? DBNull.Value;
                    }
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    int idx = 0;
                    foreach (object[] row in _pending)
                    {
                        for (int i = 0; i < _columns.Count; i++)
                        {
                            cmd.Parameters[idx++].Value = row[i] ?? DBNull.Value;
                        }
                    }
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }

    public class TableState
    {
        public long RowsCopied { get; set; }
        public string LastKeyValue { get; set; }
    }

    public class StateStore
    {
        public Dictionary<string, TableState> Tables { get; set; } = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase);
        private string _path;

        public TableState Get(string key)
        {
            return Tables.TryGetValue(key, out TableState s) ? s : null;
        }

        public TableState GetOrCreate(string key)
        {
            if (!Tables.TryGetValue(key, out TableState s))
            {
                s = new TableState();
                Tables[key] = s;
            }
            return s;
        }

        public static StateStore Load(string path)
        {
            var store = new StateStore { _path = path };
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<StateStore>(json);
                    if (loaded != null && loaded.Tables != null)
                    {
                        store.Tables = new Dictionary<string, TableState>(loaded.Tables, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // 状态文件损坏时按全新迁移处理
            }
            store._path = path;
            return store;
        }

        public void Save(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
            catch
            {
                // 状态保存失败不影响迁移主流程
            }
        }
    }
}
