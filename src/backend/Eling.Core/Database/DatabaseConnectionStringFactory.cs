using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace Eling.Core.Database;

/// <summary>
/// Turns the fields a person types into the dashboard into a provider
/// connection string, and reads a stored connection string back into those
/// fields.
///
/// The provider's own builder does the quoting, so a password containing
/// ';', '=' or quotes survives the round trip intact - which is exactly what
/// hand-assembling a connection string in the browser gets wrong.
/// </summary>
public static class DatabaseConnectionStringFactory
{
    /// <summary>The port MySQL listens on unless the connection says otherwise.</summary>
    public const int DefaultMySqlPort = 3306;

    /// <summary>
    /// Reads a stored connection string back into its fields, or returns
    /// <c>null</c> when the string cannot be read as this provider - the
    /// dashboard then has nothing to pre-fill the form with.
    /// </summary>
    public static ManualConnectionFields? Parse(DatabaseProvider provider, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        return provider switch
        {
            DatabaseProvider.MySql => ParseMySql(connectionString),
            DatabaseProvider.Sqlite => ParseSqlite(connectionString),
            _ => null,
        };
    }

    /// <summary>
    /// Builds a provider connection string from the given fields.
    ///
    /// The database is optional for MySQL: a connection without one is a
    /// server-level connection, and the dashboard lists the databases on that
    /// server instead of the tables of one of them.
    /// </summary>
    /// <exception cref="ArgumentException">A field the provider needs is missing.</exception>
    public static string Build(DatabaseProvider provider, ManualConnectionFields fields) => provider switch
    {
        DatabaseProvider.MySql => BuildMySql(fields),
        DatabaseProvider.Sqlite => BuildSqlite(fields),
        _ => throw new ArgumentException($"provider '{provider}' is not supported.", nameof(provider)),
    };

    /// <summary>
    /// Layers the fields a person just typed onto a stored connection string.
    ///
    /// The provider's builder is what gets edited, so the options the stored
    /// string carried that no field covers - an SSL mode, a charset, a
    /// timeout - survive the update. A blank field keeps what was stored:
    /// the password is never shown again, so an empty password box has to
    /// mean "keep the stored one" rather than "erase it".
    ///
    /// The database is the exception: an empty database box means "this
    /// connection is server-level", so it clears the stored database.
    /// </summary>
    /// <exception cref="ArgumentException">The result would have no host.</exception>
    public static string Update(
        DatabaseProvider provider,
        string storedConnectionString,
        ManualConnectionFields entered) => provider switch
    {
        DatabaseProvider.MySql => UpdateMySql(storedConnectionString, entered),
        DatabaseProvider.Sqlite => UpdateSqlite(storedConnectionString, entered),
        _ => throw new ArgumentException($"provider '{provider}' is not supported.", nameof(provider)),
    };

    /// <summary>
    /// Points a stored connection string at a different database.
    ///
    /// A server-level connection - one without a default database - reaches
    /// every database on the server, but an unqualified table name still
    /// needs one. Setting the database on the connection string is what makes
    /// <c>SELECT * FROM orders</c> resolve.
    /// </summary>
    public static string WithDatabase(
        DatabaseProvider provider,
        string connectionString,
        string database) => provider switch
        {
            DatabaseProvider.MySql => WithMySqlDatabase(connectionString, database),
            // A SQLite file is its own database; there is nothing to point at.
            DatabaseProvider.Sqlite => connectionString,
            _ => throw new ArgumentException($"provider '{provider}' is not supported.", nameof(provider)),
        };

    private static string WithMySqlDatabase(string connectionString, string database)
    {
        var target = NullIfBlank(database)
            ?? throw new ArgumentException("database must not be blank.", nameof(database));

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("the stored connection string is not readable as MySQL.", nameof(connectionString));
        }

        builder.Database = target;
        return builder.ConnectionString;
    }

    /// <summary>
    /// True when the connection names no database - a server-level
    /// connection, which reaches every database on the server.
    ///
    /// Only MySQL has that notion: a SQLite file is its own database.
    /// </summary>
    public static bool IsServerLevel(DatabaseProvider provider, string? connectionString)
    {
        if (provider != DatabaseProvider.MySql) return false;

        var fields = Parse(provider, connectionString);
        return fields is not null && string.IsNullOrWhiteSpace(fields.Database);
    }

    /// <summary>
    /// True when the connection names no database - a server-level connection,
    /// which reaches every database on the server. Convenience overload for a
    /// <see cref="DatabaseConnection"/>.
    /// </summary>
    public static bool IsServerLevel(DatabaseConnection connection) =>
        IsServerLevel(connection.Provider, connection.ConnectionString);

    /// <summary>
    /// Points the connection at a database, so an unqualified table name
    /// resolves. A connection that already names a database keeps it unless
    /// the caller names another one. A connection without a database (or a
    /// blank database) is returned unchanged.
    /// </summary>
    public static DatabaseConnection Target(DatabaseConnection connection, string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return connection;

        return connection with
        {
            ConnectionString = WithDatabase(
                connection.Provider,
                connection.ConnectionString,
                database)
        };
    }

    private static ManualConnectionFields? ParseMySql(string connectionString)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var server = NullIfBlank(builder.Server);
        if (server is null)
        {
            // Nothing that identifies a server: there is no form to pre-fill.
            return null;
        }

        return new ManualConnectionFields(
            server,
            (int)builder.Port,
            NullIfBlank(builder.Database),
            NullIfBlank(builder.UserID),
            NullIfBlank(builder.Password),
            null);
    }

    private static ManualConnectionFields? ParseSqlite(string connectionString)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var dataSource = NullIfBlank(builder.DataSource);
        if (dataSource is null)
        {
            return null;
        }

        return new ManualConnectionFields(null, null, null, null, null, dataSource);
    }

    private static string BuildMySql(ManualConnectionFields fields)
    {
        var host = NullIfBlank(fields.Host)
            ?? throw new ArgumentException("host must not be blank.", nameof(fields));

        var port = fields.Port ?? DefaultMySqlPort;
        if (port < 1 || port > 65535)
        {
            throw new ArgumentException("port must be between 1 and 65535.", nameof(fields));
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
        };

        var database = NullIfBlank(fields.Database);
        if (database is not null)
        {
            builder.Database = database;
        }

        var username = NullIfBlank(fields.Username);
        if (username is not null)
        {
            builder.UserID = username;
        }

        var password = NullIfBlank(fields.Password);
        if (password is not null)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static string BuildSqlite(ManualConnectionFields fields)
    {
        var filePath = NullIfBlank(fields.FilePath)
            ?? throw new ArgumentException("filePath must not be blank.", nameof(fields));

        return new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
    }

    private static string UpdateMySql(string storedConnectionString, ManualConnectionFields entered)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(storedConnectionString);
        }
        catch (ArgumentException)
        {
            // The stored string is not readable as MySQL: build a fresh one.
            return BuildMySql(entered);
        }

        // A blank field keeps what was stored, so a form that only knows the
        // password cannot wipe the host out from under it.
        var host = NullIfBlank(entered.Host) ?? NullIfBlank(builder.Server)
            ?? throw new ArgumentException("host must not be blank.", nameof(entered));
        builder.Server = host;

        if (entered.Port is not null)
        {
            var port = entered.Port.Value;
            if (port < 1 || port > 65535)
            {
                throw new ArgumentException("port must be between 1 and 65535.", nameof(entered));
            }
            builder.Port = (uint)port;
        }

        var database = NullIfBlank(entered.Database);
        if (database is null)
        {
            // An empty database box means "server-level", so the stored
            // database goes away.
            builder.Database = "";
        }
        else
        {
            builder.Database = database;
        }

        var username = NullIfBlank(entered.Username);
        if (username is not null)
        {
            builder.UserID = username;
        }

        var password = NullIfBlank(entered.Password);
        if (password is not null)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static string UpdateSqlite(string storedConnectionString, ManualConnectionFields entered)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(storedConnectionString);
        }
        catch (ArgumentException)
        {
            return BuildSqlite(entered);
        }

        // A blank field keeps what was stored.
        var filePath = NullIfBlank(entered.FilePath) ?? NullIfBlank(builder.DataSource)
            ?? throw new ArgumentException("filePath must not be blank.", nameof(entered));
        builder.DataSource = filePath;
        return builder.ToString();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
