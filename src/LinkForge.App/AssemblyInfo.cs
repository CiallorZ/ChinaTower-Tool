using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// 允许 LinkForge.App.Tests 访问 internal 成员（如控件模板部件访问器）。
// 目的：让测试能验证"明文是否进入可视化树"这类安全契约，
// 而不必把这些成员公开出去、给页面代码制造误用入口。
[assembly:InternalsVisibleTo("LinkForge.App.Tests")]
