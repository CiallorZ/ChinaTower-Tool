using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkForge.Persistence;

/// <summary>
/// 存储实现的共用判断。
/// </summary>
internal static class SqliteStoreHelper
{
    /// <summary>SQLite 主键冲突的扩展错误码。</summary>
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQLite 唯一索引冲突的扩展错误码。</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <summary>
    /// 判断异常是否为唯一约束冲突。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <returns>是否由唯一索引触发。</returns>
    /// <remarks>
    /// 靠错误码而不是消息文本：SQLite 的约束错误消息里带列名与 SQL 片段，
    /// 直接把它透给用户既不可读，也会把内部表结构暴露到界面上。
    /// </remarks>
    internal static bool IsUniqueViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteExtendedErrorCode
                    is SqliteConstraintPrimaryKey or SqliteConstraintUnique)
            {
                return true;
            }
        }

        return false;
    }
}
