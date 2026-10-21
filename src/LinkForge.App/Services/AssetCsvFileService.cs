using System.IO;
using LinkForge.App.Presentation;
using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>
/// 选中的导入文件。
/// </summary>
/// <param name="FileName">文件完整路径。</param>
/// <param name="Content">文件内容。</param>
public readonly record struct AssetCsvImportFile(string FileName, byte[] Content);

/// <summary>
/// 资产 CSV 的文件选择与读写边界（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 抽象的原因是对话框与文件系统都无法在单元测试里稳定驱动：
/// 视图模型只依赖本接口，测试用内存实现就能覆盖"导入成功 / 部分成功 / 失败 / 取消"
/// 全部路径，而真实实现只负责"选文件 + 读写字节"这一件事。
/// </para>
/// <para>
/// 读取不在本接口里做校验：字节交给 <c>AssetCsvCodec</c>，
/// 让"能不能解析"只在一处判断。
/// </para>
/// </remarks>
public interface IAssetCsvFileService
{
    /// <summary>
    /// 让用户选择并读取一个 CSV 文件。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件内容；用户取消选择时返回 null。</returns>
    Task<AssetCsvImportFile?> PickAndReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 让用户选择保存位置并写入内容。
    /// </summary>
    /// <param name="suggestedFileName">默认文件名。</param>
    /// <param name="content">要写入的字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否写出了文件（false 表示用户取消）。</returns>
    Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 使用系统文件对话框的实现。
/// </summary>
/// <remarks>
/// 文件对话框必须回到 UI 线程调用；本类的方法都在视图模型的命令里被等待，
/// 因此天然运行在 UI 线程上。刻意不在此处做 <c>Task.Run</c>：
/// 在后台线程弹对话框会直接抛异常。
/// </remarks>
public sealed class AssetCsvFileService : IAssetCsvFileService
{
    /// <inheritdoc />
    public async Task<AssetCsvImportFile?> PickAndReadAsync(
        CancellationToken cancellationToken = default)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入资产 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);

        return new AssetCsvImportFile(dialog.FileName, bytes);
    }

    /// <inheritdoc />
    public async Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var dialog = new SaveFileDialog
        {
            Title = "导出资产 CSV",
            FileName = suggestedFileName,
            DefaultExt = ".csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        await File.WriteAllBytesAsync(dialog.FileName, content, cancellationToken).ConfigureAwait(true);

        return true;
    }
}
