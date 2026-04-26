using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OpenclawChat.Models;

namespace OpenclawChat.Services;

public sealed class CreateAdminResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public UserAccount? Admin { get; init; }
    public Tenant? Tenant { get; init; }
}

public sealed class InviteResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? ActivationToken { get; init; }
    public string? Username { get; init; }
}

public sealed class LoginResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public UserAccount? User { get; init; }
}

public sealed class ActivateResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public UserAccount? User { get; init; }
}

/// <summary>
/// SQLite-backed 租户/用户存储。所有用户操作按 tenantId 隔离。
/// </summary>
public sealed class UserStore
{
    private static readonly Regex UsernamePattern = new("^[a-zA-Z][a-zA-Z0-9_-]{1,30}$", RegexOptions.Compiled);
    private static readonly Regex TenantIdPattern = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedTenantIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "login", "logout", "register", "admin", "activate", "api", "_blazor", "_framework",
        "css", "js", "images", "img", "static", "assets", "favicon.ico", "_host", "error",
    };

    private readonly string _connectionString;

    public UserStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("databasePath required", nameof(databasePath));
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        InitializeSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = Open();

        // 检测新版 schema：需要存在 tenants 表 + users.email 全局唯一。
        // 不匹配则清表重建。
        bool needsRebuild = !TableExists(conn, "tenants")
            || !ColumnExists(conn, "users", "email")
            || !UniqueIndexCoversEmailOnly(conn);

        if (needsRebuild)
        {
            using var drop = conn.CreateCommand();
            drop.CommandText = @"
DROP TABLE IF EXISTS users;
DROP TABLE IF EXISTS settings;
DROP TABLE IF EXISTS tenants;
";
            drop.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tenants (
    id TEXT PRIMARY KEY COLLATE NOCASE,
    name TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    token TEXT NOT NULL,
    origin TEXT,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    tenant_id TEXT NOT NULL COLLATE NOCASE,
    username TEXT NOT NULL COLLATE NOCASE,
    email TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT,
    agent_name TEXT NOT NULL,
    is_admin INTEGER NOT NULL DEFAULT 0,
    is_activated INTEGER NOT NULL DEFAULT 0,
    activation_token TEXT UNIQUE,
    created_at TEXT NOT NULL,
    activated_at TEXT,
    PRIMARY KEY (tenant_id, username),
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);
";
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>判断 users 表是否存在全局唯一的 email 约束（仅名称推断，在 schema 不匹配时重建即可）。</summary>
    private static bool UniqueIndexCoversEmailOnly(SqliteConnection conn)
    {
        if (!TableExists(conn, "users")) return false;

        // SQLite 中 UNIQUE列会生成一个名为 sqlite_autoindex_users_* 的索引，
        // 包含完全匹配的列名：email。检查是否存在一个仅以 email 为列的唯一索引。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list(users)";
        var indexes = new List<string>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (Convert.ToInt64(r["unique"]) != 0)
                {
                    indexes.Add(r["name"]?.ToString() ?? string.Empty);
                }
            }
        }

        foreach (var idx in indexes)
        {
            using var info = conn.CreateCommand();
            info.CommandText = $"PRAGMA index_info('{idx.Replace("'", "''")}')";
            using var ir = info.ExecuteReader();
            var cols = new List<string>();
            while (ir.Read())
            {
                cols.Add(ir["name"]?.ToString() ?? string.Empty);
            }
            if (cols.Count == 1 && string.Equals(cols[0], "email", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- 租户 ----------

    public bool HasAnyTenant()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tenants";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>按 Id 查找租户。</summary>
    public Tenant? GetTenant(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tenants WHERE id=$id COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$id", id.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapTenant(r) : null;
    }

    public IReadOnlyList<Tenant> GetAllTenants()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tenants ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<Tenant>();
        while (r.Read())
        {
            list.Add(MapTenant(r));
        }
        return list;
    }

    public static string? ValidateTenantId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "租户标识不能为空。";
        }

        var trimmed = id.Trim();
        if (!TenantIdPattern.IsMatch(trimmed))
        {
            return "租户标识仅允许小写字母、数字、横线，且以字母开头，长度 2-31。";
        }

        if (ReservedTenantIds.Contains(trimmed))
        {
            return $"\"{trimmed}\" 是保留路径，请换一个。";
        }

        return null;
    }

    /// <summary>
    /// 创建租户 + 该租户唯一管理员（一次性事务）。租户 Id 自动生成（12 位短 uuid）。
    /// </summary>
    public CreateAdminResult CreateTenantWithAdmin(
        string tenantName,
        string endpoint,
        string token,
        string? origin,
        string adminUsername,
        string adminEmail,
        string adminPassword)
    {
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            return new CreateAdminResult { Success = false, Message = "租户名称不能为空。" };
        }

        if (tenantName.Trim().Length > 60)
        {
            return new CreateAdminResult { Success = false, Message = "租户名称长度不能超过 60。" };
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new CreateAdminResult { Success = false, Message = "网关地址不能为空。" };
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new CreateAdminResult { Success = false, Message = "网关 Token 不能为空。" };
        }

        var userErr = ValidateUsernameAndPassword(adminUsername, adminPassword);
        if (userErr is not null)
        {
            return new CreateAdminResult { Success = false, Message = userErr };
        }

        if (string.IsNullOrWhiteSpace(adminEmail) || !adminEmail.Contains('@'))
        {
            return new CreateAdminResult { Success = false, Message = "管理员邮箱无效。" };
        }

        if (FindByEmail(adminEmail) is not null)
        {
            return new CreateAdminResult { Success = false, Message = "该邮箱已被使用。" };
        }

        var trimTenant = GenerateUniqueTenantId();
        var trimUser = adminUsername.Trim();
        var trimMail = adminEmail.Trim();

        var tenant = new Tenant
        {
            Id = trimTenant,
            Name = tenantName.Trim(),
            GatewayEndpoint = endpoint.Trim(),
            GatewayToken = token.Trim(),
            GatewayOrigin = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var admin = new UserAccount
        {
            TenantId = trimTenant,
            Username = trimUser,
            Email = trimMail,
            PasswordHash = PasswordHasher.Hash(adminPassword),
            AgentName = "main",
            IsAdmin = true,
            IsActivated = true,
            ActivatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO tenants(id, name, endpoint, token, origin, created_at)
VALUES($id, $n, $e, $t, $o, $c);";
                cmd.Parameters.AddWithValue("$id", tenant.Id);
                cmd.Parameters.AddWithValue("$n", tenant.Name);
                cmd.Parameters.AddWithValue("$e", tenant.GatewayEndpoint);
                cmd.Parameters.AddWithValue("$t", tenant.GatewayToken);
                cmd.Parameters.AddWithValue("$o", (object?)tenant.GatewayOrigin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", tenant.CreatedAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            InsertUser(conn, tx, admin);
            tx.Commit();
        }
        catch (SqliteException ex)
        {
            tx.Rollback();
            return new CreateAdminResult { Success = false, Message = $"创建失败：{ex.Message}" };
        }

        return new CreateAdminResult
        {
            Success = true,
            Message = "租户与管理员已创建。",
            Admin = admin,
            Tenant = tenant,
        };
    }

    // ---------- 用户查询 ----------

    public bool HasAdmin(string tenantId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE tenant_id=$t COLLATE NOCASE AND is_admin=1";
        cmd.Parameters.AddWithValue("$t", tenantId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public UserAccount? FindByUsername(string tenantId, string username)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE tenant_id=$t COLLATE NOCASE AND username=$u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$t", tenantId.Trim());
        cmd.Parameters.AddWithValue("$u", username.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUser(r) : null;
    }

    /// <summary>token 全局唯一。</summary>
    public UserAccount? FindByActivationToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE activation_token=$tok";
        cmd.Parameters.AddWithValue("$tok", token);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUser(r) : null;
    }

    /// <summary>邮箱全局唯一。</summary>
    public UserAccount? FindByEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE email=$e COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$e", email.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapUser(r) : null;
    }

    public IReadOnlyList<UserAccount> GetAllUsers(string tenantId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE tenant_id=$t COLLATE NOCASE ORDER BY created_at";
        cmd.Parameters.AddWithValue("$t", tenantId);
        using var r = cmd.ExecuteReader();
        var list = new List<UserAccount>();
        while (r.Read())
        {
            list.Add(MapUser(r));
        }
        return list;
    }

    public bool DeleteUser(string tenantId, string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE tenant_id=$t COLLATE NOCASE AND username=$u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---------- 邀请 / 激活 ----------

    public InviteResult Invite(string tenantId, string username, string email)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || GetTenant(tenantId) is null)
        {
            return new InviteResult { Success = false, Message = "租户不存在。" };
        }

        var validate = ValidateUsername(username);
        if (validate is not null)
        {
            return new InviteResult { Success = false, Message = validate };
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return new InviteResult { Success = false, Message = "请填写有效的邮箱。" };
        }

        var trimTenant = tenantId.Trim();
        var trimName = username.Trim();
        var trimMail = email.Trim();

        if (FindByUsername(trimTenant, trimName) is not null)
        {
            return new InviteResult { Success = false, Message = "用户名已存在。" };
        }

        using (var conn = Open())
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM users WHERE email=$e COLLATE NOCASE";
            check.Parameters.AddWithValue("$e", trimMail);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            {
                return new InviteResult { Success = false, Message = "该邮箱已被使用。" };
            }
        }

        var token = GenerateToken();
        var user = new UserAccount
        {
            TenantId = trimTenant,
            Username = trimName,
            Email = trimMail,
            PasswordHash = null,
            AgentName = trimName,
            IsAdmin = false,
            IsActivated = false,
            ActivationToken = token,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using (var conn = Open())
        {
            InsertUser(conn, null, user);
        }

        return new InviteResult
        {
            Success = true,
            Message = $"已为 {trimName} 创建邀请。",
            ActivationToken = token,
            Username = trimName,
        };
    }

    public ActivateResult Activate(string token, string password)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ActivateResult { Success = false, Message = "邀请链接无效。" };
        }

        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            return new ActivateResult { Success = false, Message = "密码长度至少 6 位。" };
        }

        var user = FindByActivationToken(token);
        if (user is null || user.IsActivated)
        {
            return new ActivateResult { Success = false, Message = "邀请链接已失效。" };
        }

        user.PasswordHash = PasswordHasher.Hash(password);
        user.IsActivated = true;
        user.ActivatedAt = DateTimeOffset.UtcNow;
        user.ActivationToken = null;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE users
SET password_hash=$h, is_activated=1, activation_token=NULL, activated_at=$at
WHERE tenant_id=$t COLLATE NOCASE AND username=$u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$h", user.PasswordHash);
        cmd.Parameters.AddWithValue("$at", user.ActivatedAt!.Value.ToString("o"));
        cmd.Parameters.AddWithValue("$t", user.TenantId);
        cmd.Parameters.AddWithValue("$u", user.Username);
        cmd.ExecuteNonQuery();

        return new ActivateResult { Success = true, Message = "激活成功。", User = user };
    }

    // ---------- 登录 ----------

    public LoginResult Authenticate(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return new LoginResult { Success = false, Message = "邮箱或密码不能为空。" };
        }

        var user = FindByEmail(email);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            return new LoginResult { Success = false, Message = "邮箱或密码错误。" };
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            return new LoginResult { Success = false, Message = "邮箱或密码错误。" };
        }

        if (!user.IsActivated)
        {
            return new LoginResult { Success = false, Message = "账号尚未激活。" };
        }

        return new LoginResult { Success = true, Message = "登录成功。", User = user };
    }

    // ---------- helpers ----------

    private static void InsertUser(SqliteConnection conn, SqliteTransaction? tx, UserAccount user)
    {
        using var cmd = conn.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
        cmd.CommandText = @"
INSERT INTO users(tenant_id, username, email, password_hash, agent_name, is_admin, is_activated, activation_token, created_at, activated_at)
VALUES($t, $u, $e, $h, $a, $ad, $act, $tok, $c, $at);";
        cmd.Parameters.AddWithValue("$t", user.TenantId);
        cmd.Parameters.AddWithValue("$u", user.Username);
        cmd.Parameters.AddWithValue("$e", (object?)user.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)user.PasswordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", user.AgentName);
        cmd.Parameters.AddWithValue("$ad", user.IsAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$act", user.IsActivated ? 1 : 0);
        cmd.Parameters.AddWithValue("$tok", (object?)user.ActivationToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", user.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$at", user.ActivatedAt is null ? DBNull.Value : user.ActivatedAt.Value.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static UserAccount MapUser(IDataRecord r)
    {
        return new UserAccount
        {
            TenantId = r["tenant_id"]?.ToString() ?? string.Empty,
            Username = r["username"]?.ToString() ?? string.Empty,
            Email = r["email"] as string,
            PasswordHash = r["password_hash"] as string,
            AgentName = r["agent_name"]?.ToString() ?? string.Empty,
            IsAdmin = Convert.ToInt64(r["is_admin"]) != 0,
            IsActivated = Convert.ToInt64(r["is_activated"]) != 0,
            ActivationToken = r["activation_token"] as string,
            CreatedAt = DateTimeOffset.Parse(r["created_at"]!.ToString()!),
            ActivatedAt = r["activated_at"] is string s && !string.IsNullOrEmpty(s)
                ? DateTimeOffset.Parse(s)
                : null,
        };
    }

    private static Tenant MapTenant(IDataRecord r)
    {
        return new Tenant
        {
            Id = r["id"]?.ToString() ?? string.Empty,
            Name = (r["name"] as string) ?? string.Empty,
            GatewayEndpoint = r["endpoint"]?.ToString() ?? string.Empty,
            GatewayToken = r["token"]?.ToString() ?? string.Empty,
            GatewayOrigin = r["origin"] as string,
            CreatedAt = DateTimeOffset.Parse(r["created_at"]!.ToString()!),
        };
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private string GenerateUniqueTenantId()
    {
        for (var i = 0; i < 16; i++)
        {
            var candidate = Guid.NewGuid().ToString("N")[..12];
            if (ReservedTenantIds.Contains(candidate))
            {
                continue;
            }
            if (GetTenant(candidate) is null)
            {
                return candidate;
            }
        }
        return Guid.NewGuid().ToString("N");
    }

    private static string? ValidateUsernameAndPassword(string username, string password)
    {
        var u = ValidateUsername(username);
        if (u is not null)
        {
            return u;
        }

        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            return "密码长度至少 6 位。";
        }

        return null;
    }

    private static string? ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "用户名不能为空。";
        }

        if (!UsernamePattern.IsMatch(username.Trim()))
        {
            return "用户名仅允许字母、数字、下划线、横线，且以字母开头，长度 2-31。";
        }

        return null;
    }
}
