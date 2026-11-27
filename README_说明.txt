Muse C# 版（WinForms + WebView2）——从 py 版完整移植
========================================================

一、为什么改 C#
py 版用 PyInstaller 打包后体积大，主要是 Python 解释器 + PyQt6/QtWebEngine
（约 150MB 以上）+ 内嵌的 mihomo.exe（61MB）+ geoip.metadb（8.4MB），
成品常常 230MB+。C# 版改动：
  * 界面改用系统自带的 WebView2（Edge 内核），不再打包整个浏览器；
  * 运行库用系统 .NET Framework 4.8，不再打包 Python；
  * mihomo.exe / geoip.metadb 照 py 版的做法继续内嵌在 exe 里（编译时打进
    资源），第一次运行时自动释放到 Muse数据/代理核心/；已经有且大小对得上
    就不再重复释放。
成品：Muse.exe 约 72MB（其中 mihomo 61MB + geoip 库 8.4MB），仍比
py 打包版 230MB+ 小三分之二；发给别人不用再另拷文件，单个 exe 就能跑。

二、怎么编译（在 Windows 电脑上）
方法 A（推荐，不用装 SDK）：双击 build.bat
  * 自动找 csc.exe（优先 Visual Studio 自带的 Roslyn 编译器，找不到就用
    .NET Framework 自带的），编译 src/*.cs，输出到 bin\Muse.exe，
    并把 lib/*.dll 一起拷到 bin\。
方法 B：用 Visual Studio 打开 Muse.csproj，选 Release 编译。
要求：Windows 10/11 + .NET Framework 4.8（Win10 1903 以后一般自带）+
WebView2 Runtime（随 Edge 浏览器自带，一般都有）。

三、怎么用（和 py 版数据完全兼容）
  1. 把 bin\Muse.exe 和 bin\ 下的 5 个 DLL 一起拷到旧版工具所在的文件夹
     （也就是 Muse数据 文件夹的上一级），直接运行即可。
     程序会读同一个 Muse数据/代理设置.json、账号列表.json，
     账号登录状态、订阅、锁定地区都不用重设。
  2. 发给别人用也不用另拷文件：mihomo.exe 和 geoip.metadb 已内嵌在 exe 里，
     第一次运行自动释放到 Muse数据/代理核心/；以前旧版工具的
     Muse数据/代理核心/ 下如果已有这两个文件（且大小对得上），直接复用，
     不重复释放。
  3. 首次运行如果提示浏览器内核初始化失败，装一下 WebView2 Runtime
     （微软官网免费下载，搜 "WebView2 Runtime"）。

四、和 py 版行为一致的地方
  * 三个勾选项原样保留：启用内置代理核心 / 线路失效自动切换 / 启用本地代理，
    判定链完全一样（核心优先 -> 系统代理/PAC/本机端口 -> 直连 -> 黑洞拦截）。
  * 地区锁定、10 分钟断网才切换、5 秒巡检拉回锁定地区、20 秒核心守卫、
    订阅合并去重、配置超 24 小时自动刷新、流量显示、时钟、多账号隔离、
    下载分流（http 甩系统浏览器、blob 弹保存框）、登录掉线提示、崩溃重载，
    全部按 py 版逻辑移植。

五、和 py 版不一样的地方（有意改的）
  1. 核心 secret 不再写死在源码里：优先读现有 config.yaml 里的 secret 行，
     没有配置时随机生成一个，写订阅配置时一并写入。
  2. 运行时缓存清理：WebView2 没有“只清 HTTP 缓存”的接口（清全部会把登录
     也清掉），所以缓存超 1.5GB 时只提示并记个标记，下次启动前直接删缓存
     文件夹，和 py 版启动时删文件夹的行为一致。
  3. 窗口位置改存成 x,y,w,h,state 文本（py 版是 Qt 的 base64 几何数据，
     WinForms 读不了，所以不共用这一项，首次运行窗口居中显示）。
  4. 点最小化按钮后窗口藏到右下角托盘（任务栏上不留图标），双击托盘图标
     恢复窗口，右键托盘图标可以显示窗口或退出程序。
  5. 代理设置、选择线路两个对话框重新排版：长文字自动换行不再重叠，
     字体用微软雅黑，和 py 版观感一致。

六、自动更新
  * 顶栏有「检查更新」按钮，点一下就查；每天第一次启动也会自动查一次。
  * 有新版会弹窗问你要不要更新，点了就自动下载源码包、在本机重新
    编译、替换后重启，全程不用你再下 zip。
  * 更新走 GitHub 公开仓库 waxtdm/muse-csharp，更新信息在 version.json。
    仓库里只有文本源码（不含 lib/ 的 DLL 和 res/ 的 mihomo.exe），
    编译时从本机已安装的目录复用这两个文件。
  * 更新失败（比如下不到包、编不过）不会动旧版本，照常能用。

七、文件清单
  build.bat            一键编译脚本
  Muse.csproj          Visual Studio 工程文件
  src\*.cs             全部源码（App/Regions/ProxyCore/ProxyDetect/
                       Program/MainForm/PopupForm/ProxyDialog/NodePickerDialog/UpdateCheck）
  lib\                 编译期依赖（WebView2、YamlDotNet、Newtonsoft.Json、
                       WebView2Loader.dll），编译完也要和 exe 放一起。
