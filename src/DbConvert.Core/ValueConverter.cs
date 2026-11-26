using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DbConvert
{
    /// <summary>某一行因数据问题需要跳过。</summary>
    public class SkipRowException : Exception
    {
        public SkipRowException(string message) : base(message) { }
    }

    /// <summary>把源值转换成目标驱动能接受的 CLR 值，并做长度/精度/空串等语义处理。</summary>
    public class ValueConverter
    {
        private readonly Dialect _target;
        private readonly OptionsConfig _opt;
        private readonly Action<string> _log;

        public ValueConverter(Dialect target, OptionsConfig opt, Action<string> log)
        {
            _target = target;
            _opt = opt ?? new OptionsConfig();
            _log = log;
        }

        public object ConvertValue(object value, ColumnDef src, ColumnDef tgt)
        {
            if (value == null || value is DBNull) return DBNull.Value;

            // 空字符串：Oracle/达梦把 '' 当 NULL，保持语义一致
            if (value is string && ((string)value).Length == 0 && _opt.EmptyStringAsNull)
            {
                return DBNull.Value;
            }

            switch (tgt.Kind)
            {
                case ColumnKind.Boolean:
                    return ConvertBool(value, src, tgt);
                case ColumnKind.TinyInt:
                case ColumnKind.SmallInt:
                case ColumnKind.Int:
                case ColumnKind.BigInt:
                    return ConvertInteger(value, src, tgt);
                case ColumnKind.Decimal:
                case ColumnKind.Money:
                    return ConvertDecimal(value, src, tgt);
                case ColumnKind.Float:
                case ColumnKind.Double:
                    return ConvertDouble(value, src, tgt);
                case ColumnKind.Char:
                case ColumnKind.NChar:
                case ColumnKind.VarChar:
                case ColumnKind.NVarChar:
                case ColumnKind.Text:
                case ColumnKind.Enum:
                case ColumnKind.Json:
                case ColumnKind.Xml:
                case ColumnKind.Interval:
                case ColumnKind.Unknown:
                    return ConvertString(value, src, tgt);
                case ColumnKind.Date:
                case ColumnKind.DateTime:
                case ColumnKind.TimestampTz:
                    return ConvertDateTime(value, src, tgt);
                case ColumnKind.Time:
                    return ConvertTime(value, tgt);
                case ColumnKind.Binary:
                case ColumnKind.Blob:
                    return ConvertBinary(value, src, tgt);
                case ColumnKind.Guid:
                    return ConvertGuid(value, tgt);
                default:
                    return value;
            }
        }

        private object ConvertBool(object value, ColumnDef src, ColumnDef tgt)
        {
            bool b;
            if (value is bool) b = (bool)value;
            else if (value is string)
            {
                string s = ((string)value).Trim().ToLowerInvariant();
                b = s == "1" || s == "true" || s == "t" || s == "yes" || s == "y";
            }
            else
            {
                decimal d = ToDecimal(value);
                b = d != 0;
            }

            switch (_target.Family)
            {
                case DialectFamily.Postgres: return b;
                case DialectFamily.SqlServer: return b;
                case DialectFamily.Sqlite: return b ? 1L : 0L;
                default: return b ? (short)1 : (short)0;
            }
        }

        private object ConvertInteger(object value, ColumnDef src, ColumnDef tgt)
        {
            if (value is int || value is long || value is short || value is sbyte || value is byte || value is ushort || value is uint || value is ulong)
            {
                // 同族整数直接透传，避免无谓精度损失
                if (tgt.Kind == ColumnKind.BigInt) return ToInt64(value);
                if (tgt.Kind == ColumnKind.Int) return ToInt64(value);
                if (tgt.Kind == ColumnKind.SmallInt) return ToInt64(value);
                return ToInt64(value);
            }
            if (value is bool) return ((bool)value) ? 1L : 0L;
            decimal d = ToDecimal(value);
            if (d != decimal.Truncate(d) && _opt.OverflowPolicy != "truncate")
            {
                // Oracle 的 NUMBER(10,0) 场景允许四舍五入，这里按取整处理并记录
                d = decimal.Round(d, 0, MidpointRounding.AwayFromZero);
            }
            else if (d != decimal.Truncate(d))
            {
                d = decimal.Truncate(d);
            }
            CheckRange(d, tgt);
            return (long)d;
        }

        private void CheckRange(decimal d, ColumnDef tgt)
        {
            decimal min = decimal.MinValue;
            decimal max = decimal.MaxValue;
            switch (tgt.Kind)
            {
                case ColumnKind.TinyInt:
                    min = tgt.IsUnsigned ? 0 : -128;
                    max = tgt.IsUnsigned ? 255 : 127;
                    break;
                case ColumnKind.SmallInt:
                    min = tgt.IsUnsigned ? 0 : -32768;
                    max = tgt.IsUnsigned ? 65535 : 32767;
                    break;
                case ColumnKind.Int:
                    min = tgt.IsUnsigned ? 0 : int.MinValue;
                    max = tgt.IsUnsigned ? 4294967295m : int.MaxValue;
                    break;
                case ColumnKind.BigInt:
                    min = tgt.IsUnsigned ? 0 : long.MinValue;
                    max = tgt.IsUnsigned ? 18446744073709551615m : long.MaxValue;
                    if (d > (decimal)ulong.MaxValue && tgt.IsUnsigned) max = (decimal)ulong.MaxValue;
                    break;
            }
            if (d < min || d > max)
            {
                throw new OverflowException("值 " + d + " 超出目标列 " + tgt.Name + " 范围 [" + min + "," + max + "]");
            }
        }

        private object ConvertDecimal(object value, ColumnDef src, ColumnDef tgt)
        {
            decimal d = ToDecimal(value);
            if (tgt.Scale.HasValue)
            {
                d = decimal.Round(d, Math.Min(tgt.Scale.Value, 28), MidpointRounding.AwayFromZero);
            }
            if (tgt.Precision.HasValue && tgt.Scale.HasValue)
            {
                decimal limit = (decimal)Math.Pow(10, tgt.Precision.Value - tgt.Scale.Value);
                if (Math.Abs(d) >= limit)
                {
                    if (_opt.OverflowPolicy == "truncate") d = (limit - (decimal)Math.Pow(10, -tgt.Scale.Value)) * Math.Sign(d);
                    else throw new OverflowException("值 " + d + " 超出目标列 " + tgt.Name + " 精度 " + tgt.Precision + "," + tgt.Scale);
                }
            }
            return d;
        }

        private object ConvertDouble(object value, ColumnDef src, ColumnDef tgt)
        {
            if (value is double || value is float) return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (value is decimal) return (double)(decimal)value;
            return double.Parse(ToStringValue(value), CultureInfo.InvariantCulture);
        }

        private object ConvertString(object value, ColumnDef src, ColumnDef tgt)
        {
            string s;
            if (value is byte[])
            {
                s = Convert.ToBase64String((byte[])value);
            }
            else if (value is DateTime)
            {
                s = ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            }
            else if (value is bool)
            {
                s = ((bool)value) ? "1" : "0";
            }
            else
            {
                s = ToStringValue(value);
            }

            if (s == null) return DBNull.Value;

            int? limit = null;
            if (tgt.Kind == ColumnKind.Char || tgt.Kind == ColumnKind.VarChar ||
                tgt.Kind == ColumnKind.NChar || tgt.Kind == ColumnKind.NVarChar)
            {
                limit = tgt.Length;
            }
            if (limit.HasValue && limit.Value > 0 && s.Length > limit.Value)
            {
                switch ((_opt.OverflowPolicy ?? "fail").ToLowerInvariant())
                {
                    case "truncate":
                        s = s.Substring(0, limit.Value);
                        _log?.Invoke("warn: 截断超长字符串到 " + limit.Value + " 字符（目标列 " + tgt.Name + "）");
                        break;
                    case "skiprow":
                        throw new SkipRowException("字符串长度 " + s.Length + " 超过目标列 " + tgt.Name + " 上限 " + limit.Value);
                    default:
                        throw new OverflowException("字符串长度 " + s.Length + " 超过目标列 " + tgt.Name + " 上限 " + limit.Value);
                }
            }
            return s;
        }

        private object ConvertDateTime(object value, ColumnDef src, ColumnDef tgt)
        {
            DateTime dt;
            if (value is DateTime) dt = (DateTime)value;
            else if (value is DateTimeOffset) dt = ((DateTimeOffset)value).DateTime;
            else if (value is string)
            {
                dt = ParseDateTime((string)value);
            }
            else
            {
                throw new InvalidCastException("无法把 " + value.GetType().Name + " 转成日期时间（列 " + tgt.Name + "）");
            }

            if (_target.Family == DialectFamily.MySql || _target.Family == DialectFamily.Sqlite)
            {
                if (dt.Year < 1000 || dt.Year > 9999)
                {
                    _log?.Invoke("warn: 目标列 " + tgt.Name + " 不支持年份 " + dt.Year + "，写入 NULL");
                    return DBNull.Value;
                }
            }
            if (tgt.Kind == ColumnKind.Date)
            {
                if (_target.Family == DialectFamily.Sqlite)
                {
                    return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                return dt.Date;
            }
            if (_target.Family == DialectFamily.Sqlite)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            if (dt.Kind == DateTimeKind.Local && tgt.Kind == ColumnKind.TimestampTz)
            {
                return new DateTimeOffset(dt).ToUniversalTime();
            }
            return dt;
        }

        private static DateTime ParseDateTime(string s)
        {
            s = s.Trim();
            string[] formats =
            {
                "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
                "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd", "yyyyMMddHHmmss"
            };
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)) return dt;
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            throw new InvalidCastException("无法解析日期时间 '" + s + "'");
        }

        private object ConvertTime(object value, ColumnDef tgt)
        {
            if (value is string)
            {
                string s = (string)value;
                if (_target.Family == DialectFamily.Oracle) return s;
                if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out TimeSpan ts)) return ts;
                return s;
            }
            if (value is TimeSpan)
            {
                TimeSpan ts = (TimeSpan)value;
                if (_target.Family == DialectFamily.Oracle) return Format(ts);
                if (_target.Family == DialectFamily.Sqlite) return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                return ts;
            }
            if (value is DateTime)
            {
                TimeSpan ts = ((DateTime)value).TimeOfDay;
                if (_target.Family == DialectFamily.Oracle) return Format(ts);
                if (_target.Family == DialectFamily.Sqlite) return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                return ts;
            }
            string str = ToStringValue(value);
            if (_target.Family == DialectFamily.Oracle) return str;
            if (TimeSpan.TryParse(str, CultureInfo.InvariantCulture, out TimeSpan parsed)) return parsed;
            return str;
        }

        private static string Format(TimeSpan ts)
        {
            return ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        private object ConvertBinary(object value, ColumnDef src, ColumnDef tgt)
        {
            byte[] bytes;
            if (value is byte[]) bytes = (byte[])value;
            else if (value is string) bytes = Encoding.UTF8.GetBytes((string)value);
            else bytes = Encoding.UTF8.GetBytes(ToStringValue(value));

            if (tgt.Kind == ColumnKind.Binary && tgt.Length.HasValue && bytes.Length > tgt.Length.Value)
            {
                if (_opt.OverflowPolicy == "truncate") bytes = TrimBytes(bytes, tgt.Length.Value);
                else throw new OverflowException("二进制长度 " + bytes.Length + " 超过目标列 " + tgt.Name + " 上限 " + tgt.Length);
            }
            return bytes;
        }

        private static byte[] TrimBytes(byte[] b, int len)
        {
            var r = new byte[len];
            Array.Copy(b, r, Math.Min(len, b.Length));
            return r;
        }

        private object ConvertGuid(object value, ColumnDef tgt)
        {
            if (value is Guid)
            {
                if (_target.Family == DialectFamily.Postgres || _target.Family == DialectFamily.SqlServer) return value;
                return value.ToString();
            }
            string s = ToStringValue(value);
            if (_target.Family == DialectFamily.Postgres || _target.Family == DialectFamily.SqlServer)
            {
                if (Guid.TryParse(s, out Guid g)) return g;
            }
            return s;
        }

        private decimal ToDecimal(object value)
        {
            if (value is decimal) return (decimal)value;
            if (value is int) return (decimal)(int)value;
            if (value is long) return (decimal)(long)value;
            if (value is short) return (decimal)(short)value;
            if (value is byte) return (decimal)(byte)value;
            if (value is sbyte) return (decimal)(sbyte)value;
            if (value is ulong) return (decimal)(ulong)value;
            if (value is uint) return (decimal)(uint)value;
            if (value is double) return (decimal)(double)value;
            if (value is float) return (decimal)(float)value;
            if (value is bool) return ((bool)value) ? 1m : 0m;
            string s = ToStringValue(value);
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)) return d;
            throw new InvalidCastException("无法把 '" + s + "' 转成数值");
        }

        private long ToInt64(object value)
        {
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            if (value is short) return (short)value;
            if (value is byte) return (byte)value;
            if (value is sbyte) return (sbyte)value;
            if (value is ulong) return (long)(ulong)value;
            if (value is uint) return (uint)value;
            if (value is bool) return ((bool)value) ? 1 : 0;
            if (value is decimal) return (long)(decimal)value;
            string s = ToStringValue(value);
            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out long l)) return l;
            return (long)decimal.Parse(s, CultureInfo.InvariantCulture);
        }

        public static string ToStringValue(object value)
        {
            if (value == null || value is DBNull) return null;
            if (value is string) return (string)value;
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("G17", CultureInfo.InvariantCulture);
            if (value is float) return ((float)value).ToString("G9", CultureInfo.InvariantCulture);
            if (value is bool) return ((bool)value) ? "1" : "0";
            if (value is byte[]) return Convert.ToBase64String((byte[])value);
            if (value is TimeSpan) return ((TimeSpan)value).ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
            if (value is Guid) return value.ToString();
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>把值归一化成与库无关的字符串，用于两侧校验和比较。</summary>
        public static string Normalize(object value, ColumnDef tgt)
        {
            return Normalize(value, tgt, false);
        }

        /// <summary>emptyAsNull=true 时把空串与 NULL 视为同一个值（Oracle/达梦的语义）。</summary>
        public static string Normalize(object value, ColumnDef tgt, bool emptyAsNull)
        {
            if (value == null || value is DBNull) return "\u0001NULL";
            if (emptyAsNull && value is string && ((string)value).Length == 0) return "\u0001NULL";
            switch (tgt.Kind)
            {
                case ColumnKind.Boolean:
                    return (value is bool ? (bool)value : ToStringValue(value) == "1") ? "1" : "0";
                case ColumnKind.Date:
                    return ToDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case ColumnKind.DateTime:
                case ColumnKind.TimestampTz:
                {
                    int p = Math.Min(tgt.DateTimePrecision ?? 7, 7);
                    string fmt = p <= 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss." + new string('f', p);
                    return ToDateTime(value).ToString(fmt, CultureInfo.InvariantCulture);
                }
                case ColumnKind.Time:
                    if (value is TimeSpan) return ((TimeSpan)value).ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
                    return ToStringValue(value);
                case ColumnKind.Decimal:
                case ColumnKind.Money:
                {
                    decimal d = value is decimal ? (decimal)value : decimal.Parse(ToStringValue(value), CultureInfo.InvariantCulture);
                    int scale = tgt.Scale ?? 0;
                    return d.ToString("F" + Math.Min(scale, 28), CultureInfo.InvariantCulture);
                }
                case ColumnKind.Float:
                case ColumnKind.Double:
                {
                    double d = value is double ? (double)value : double.Parse(ToStringValue(value), CultureInfo.InvariantCulture);
                    return d.ToString("G10", CultureInfo.InvariantCulture);
                }
                case ColumnKind.Binary:
                case ColumnKind.Blob:
                {
                    byte[] b = value as byte[];
                    if (b == null) return ToStringValue(value);
                    var sb = new StringBuilder(b.Length * 2);
                    foreach (byte x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
                default:
                    return ToStringValue(value) ?? "\u0001NULL";
            }
        }

        private static DateTime ToDateTime(object value)
        {
            if (value is DateTime) return (DateTime)value;
            if (value is DateTimeOffset) return ((DateTimeOffset)value).DateTime;
            return ParseDateTime(ToStringValue(value));
        }
    }
}
