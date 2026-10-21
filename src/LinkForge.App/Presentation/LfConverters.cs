using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LinkForge.App.Presentation;

/// <summary>
/// 布尔取反。
/// </summary>
/// <remarks>
/// 存在的理由很实际：大量控件的可用性条件与命令的 <c>CanExecute</c> 正好相反
/// （例如"运行中禁用输入"），没有这个转换器时只能在视图模型里再暴露一份
/// 语义重复的 <c>IsNotRunning</c>，两处布尔必然有一天不一致。
/// </remarks>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : false;
}

/// <summary>
/// 布尔取反后转可见性（true → Collapsed）。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}

/// <summary>
/// 非空（字符串或集合）转可见性。
/// </summary>
/// <remarks>
/// 用于"只在有反馈文本时才显示提示条"这类场景。
/// 集合分支是必要的：绑到空的 <c>ObservableCollection</c> 时它也应当隐藏，
/// 否则页面会留下一条没有内容的边框。
/// </remarks>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        IsNonEmpty(value) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("非空转可见性为单向转换。");

    private static bool IsNonEmpty(object? value) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        ICollection collection => collection.Count > 0,
        IEnumerable enumerable => enumerable.GetEnumerator().MoveNext(),
        _ => true
    };
}
