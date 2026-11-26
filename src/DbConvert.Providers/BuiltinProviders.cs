using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace DbConvert.Providers
{
    /// <summary>把随程序一起发布的内置 ADO.NET 驱动注册到 DbProviderFactories，供 Core 反射式使用。</summary>
    public static class BuiltinProviders
    {
        public static void Register()
        {
            SafeRegister("Microsoft.Data.Sqlite", SqliteFactory.Instance);
            SafeRegister("MySqlConnector", MySqlConnectorFactory.Instance);
            SafeRegister("Npgsql", NpgsqlFactory.Instance);
            SafeRegister("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
            SafeRegister("Oracle.ManagedDataAccess.Client", OracleClientFactory.Instance);

            // 常见别名，方便在配置里直接写 mySql / sqlite 等
            SafeRegister("System.Data.SQLite", SqliteFactory.Instance);
            SafeRegister("MySql.Data.MySqlClient", MySqlConnectorFactory.Instance);
            SafeRegister("Oracle.ManagedDataAccess", OracleClientFactory.Instance);
        }

        private static void SafeRegister(string invariant, DbProviderFactory factory)
        {
            try
            {
                DbProviderFactories.RegisterFactory(invariant, factory);
            }
            catch
            {
                // 已注册过就忽略
            }
        }

        public static bool IsRegistered(string invariant)
        {
            try
            {
                DbProviderFactories.GetFactory(invariant);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
