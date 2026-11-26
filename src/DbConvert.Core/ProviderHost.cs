using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DbConvert
{
    /// <summary>驱动注册与连接创建。内置驱动由 DbConvert.Providers 注册，达梦/金仓/神通等外部驱动按配置动态加载。</summary>
    public static class ProviderHost
    {
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> ProbeDirs = new List<string>();

        static ProviderHost()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string simple = new AssemblyName(args.Name).Name;
            foreach (string dir in ProbeDirs)
            {
                string candidate = Path.Combine(dir, simple + ".dll");
                if (File.Exists(candidate))
                {
                    try { return Assembly.LoadFrom(candidate); }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>外部驱动目录，加入程序集探测路径。</summary>
        public static void AddProbeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            dir = Path.GetFullPath(dir);
            lock (SyncRoot)
            {
                if (!ProbeDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) ProbeDirs.Add(dir);
            }
        }

        public static string DefaultInvariant(string kind)
        {
            string key = Dialects.NormalizeKind(kind);
            switch (key)
            {
                case "mysql": return "MySqlConnector";
                case "postgres": return "Npgsql";
                case "oracle": return "Oracle.ManagedDataAccess.Client";
                case "dm": return "DmProvider";
                case "sqlserver": return "Microsoft.Data.SqlClient";
                case "sqlite": return "Microsoft.Data.Sqlite";
                default: return "MySqlConnector";
            }
        }

        /// <summary>优先使用配置的外部工厂，其次内置驱动。</summary>
        public static DbProviderFactory GetFactory(EndpointConfig ep)
        {
            string invariant = DefaultInvariant(ep.Kind);

            if (!string.IsNullOrWhiteSpace(ep.ProviderAssembly))
            {
                return EnsureExternalFactory(ep.ProviderAssembly, ep.ProviderType, invariant);
            }

            try
            {
                var f = DbProviderFactories.GetFactory(invariant);
                if (f != null) return f;
            }
            catch { }

            // 内置驱动不可用时，尝试在输出目录/ providers 目录里找同名外部驱动（达梦、金仓常有独立 DLL）
            foreach (string dll in CandidateAssemblies(ep.Kind))
            {
                string path = LocateAssembly(dll);
                if (path != null)
                {
                    try { return EnsureExternalFactory(path, ep.ProviderType, invariant); }
                    catch { }
                }
            }

            throw new InvalidOperationException(
                "找不到 " + Dialects.Get(ep.Kind).Display + " 的 ADO.NET 驱动（" + invariant + "）。" +
                "请把驱动 DLL 放到程序目录或 providers 子目录，并在配置里填 providerAssembly（可选 providerType）。");
        }

        private static IEnumerable<string> CandidateAssemblies(string kind)
        {
            string key = Dialects.NormalizeKind(kind);
            switch (key)
            {
                case "dm":
                    yield return "DmProvider.dll";
                    yield return "DmProvider.Core.dll";
                    break;
                case "kingbase":
                    yield return "Kdbndp.dll";
                    yield return "KingbaseDataProvider.dll";
                    break;
                case "postgres":
                    yield return "Npgsql.dll";
                    break;
                case "mysql":
                    yield return "MySqlConnector.dll";
                    yield return "MySql.Data.dll";
                    break;
                case "oracle":
                    yield return "Oracle.ManagedDataAccess.dll";
                    break;
                case "sqlserver":
                    yield return "Microsoft.Data.SqlClient.dll";
                    break;
                case "sqlite":
                    yield return "Microsoft.Data.Sqlite.dll";
                    break;
            }
        }

        private static string LocateAssembly(string fileName)
        {
            string baseDir = AppContext.BaseDirectory;
            string[] roots = new[]
            {
                baseDir,
                Path.Combine(baseDir, "providers"),
                Path.Combine(baseDir, "lib")
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string p = Path.Combine(root, fileName);
                if (File.Exists(p)) return p;
                try
                {
                    foreach (string f in Directory.GetFiles(root, fileName, SearchOption.AllDirectories)) return f;
                }
                catch { }
            }
            return null;
        }

        private static DbProviderFactory EnsureExternalFactory(string assemblyPath, string factoryType, string invariant)
        {
            if (string.IsNullOrEmpty(invariant)) throw new ArgumentException("invariant 为空");

            string full = Path.GetFullPath(assemblyPath);
            string dir = Path.GetDirectoryName(full);
            AddProbeDir(dir);

            lock (SyncRoot)
            {
                if (Registered.Contains(invariant))
                {
                    try { return DbProviderFactories.GetFactory(invariant); } catch { Registered.Remove(invariant); }
                }

                Assembly asm = Assembly.LoadFrom(full);
                Type type = null;
                if (!string.IsNullOrWhiteSpace(factoryType))
                {
                    type = asm.GetType(factoryType, false, true);
                    if (type == null)
                    {
                        throw new InvalidOperationException("程序集 " + Path.GetFileName(full) + " 中找不到类型 " + factoryType);
                    }
                }
                else
                {
                    type = asm.GetExportedTypes()
                        .FirstOrDefault(t => typeof(DbProviderFactory).IsAssignableFrom(t) && !t.IsAbstract);
                    if (type == null)
                    {
                        throw new InvalidOperationException("程序集 " + Path.GetFileName(full) + " 中找不到 DbProviderFactory 实现，请指定 providerType");
                    }
                }

                object instance = null;
                PropertyInfo pi = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (pi != null) instance = pi.GetValue(null);
                if (instance == null)
                {
                    FieldInfo fi = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (fi != null) instance = fi.GetValue(null);
                }
                if (instance == null)
                {
                    instance = Activator.CreateInstance(type);
                }

                DbProviderFactory factory = instance as DbProviderFactory;
                if (factory == null) throw new InvalidOperationException(type.FullName + " 不是 DbProviderFactory");

                try { DbProviderFactories.RegisterFactory(invariant, factory); } catch { }
                Registered.Add(invariant);
                return factory;
            }
        }

        public static DbConnection Open(EndpointConfig ep)
        {
            if (ep == null) throw new ArgumentNullException(nameof(ep));
            string cs = ConnectionStrings.Resolve(ep);
            if (string.IsNullOrWhiteSpace(cs)) throw new ArgumentException("连接串为空（kind=" + ep.Kind + "）");

            DbProviderFactory factory = GetFactory(ep);
            DbConnection conn = factory.CreateConnection();
            if (conn == null) throw new InvalidOperationException("驱动无法创建连接对象：" + ep.Kind);
            conn.ConnectionString = cs;
            conn.Open();
            return conn;
        }

        public static string ServerInfo(DbConnection conn)
        {
            try
            {
                string v = conn.ServerVersion;
                string db = "";
                try { db = conn.Database; } catch { }
                return string.IsNullOrEmpty(db) ? v : v + " / " + db;
            }
            catch
            {
                return "?";
            }
        }
    }

    /// <summary>按字段拼装连接串，便于 GUI 使用；也可以直接给完整连接串。</summary>
    public static class ConnectionStrings
    {
        public static string Resolve(EndpointConfig ep)
        {
            if (!string.IsNullOrWhiteSpace(ep.ConnectionString)) return ep.ConnectionString;
            string key = Dialects.NormalizeKind(ep.Kind);
            int port = ep.Port;
            string host = string.IsNullOrWhiteSpace(ep.Host) ? "127.0.0.1" : ep.Host;

            switch (key)
            {
                case "mysql":
                    if (port <= 0) port = 3306;
                    return "Server=" + host + ";Port=" + port + ";Database=" + ep.Database + ";User ID=" + ep.User +
                           ";Password=" + ep.Password + ";CharSet=utf8mb4;Allow User Variables=true;Connection Timeout=30;Default Command Timeout=0";
                case "postgres":
                    if (port <= 0) port = 5432;
                    return "Host=" + host + ";Port=" + port + ";Database=" + ep.Database + ";Username=" + ep.User +
                           ";Password=" + ep.Password + ";Timeout=30;Command Timeout=0";
                case "oracle":
                    if (port <= 0) port = 1521;
                    return "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=" + port +
                           "))(CONNECT_DATA=(SERVICE_NAME=" + ep.Database + ")));User Id=" + ep.User + ";Password=" + ep.Password + ";";
                case "dm":
                    if (port <= 0) port = 5236;
                    return "Server=" + host + ";Port=" + port + ";User Id=" + ep.User + ";Password=" + ep.Password +
                           ";Schema=" + (string.IsNullOrEmpty(ep.Schema) ? ep.Database : ep.Schema) + ";";
                case "sqlserver":
                    if (port <= 0) port = 1433;
                    if (string.IsNullOrEmpty(ep.User))
                        return "Server=" + host + "," + port + ";Database=" + ep.Database + ";Integrated Security=true;TrustServerCertificate=true;";
                    return "Server=" + host + "," + port + ";Database=" + ep.Database + ";User ID=" + ep.User +
                           ";Password=" + ep.Password + ";TrustServerCertificate=true;";
                case "sqlite":
                    return "Data Source=" + (string.IsNullOrEmpty(ep.Database) ? ep.ConnectionString : ep.Database) + ";";
                default:
                    return ep.ConnectionString;
            }
        }

        public static int DefaultPort(string kind)
        {
            // 金仓走 PG 协议但端口不同，先按原始写法判断
            string raw = (kind ?? "").Trim().ToLowerInvariant();
            if (raw == "kingbase" || raw == "kingbasees" || raw == "kdb") return 54321;
            switch (Dialects.NormalizeKind(kind))
            {
                case "mysql": return 3306;
                case "postgres": return 5432;
                case "oracle": return 1521;
                case "dm": return 5236;
                case "sqlserver": return 1433;
                default: return 0;
            }
        }
    }
}
