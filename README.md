# Muse C# 版

Muse 桌面工具的 C# 移植版：WinForms + WebView2，多账号浏览器 + 内置 mihomo（Clash Meta）代理核心。

## 这是什么

- 给 muse.ai 用的多账号浏览器，每个账号独立 WebView2 环境隔离防串号
- 内置 mihomo 代理核心，订阅节点合并、测速、地区锁定（锁定后只在该地区内切换）
- 数据与 py 版完全兼容：同一 `Muse数据/` 目录、同一 `代理设置.json`

## 自动更新

程序顶栏有「检查更新」按钮，每天首次启动也会自动查一次。
更新信息在 `version.json` 里；有新版时程序从本仓库拉源码包，在本机重新编译后替换并重启。
仓库里只有文本源码，不含 `lib/` 的 DLL 和 `res/` 的 mihomo.exe / geoip.metadb——
编译时从本机已安装的目录复用这两个文件，首次安装请用完整安装包。

## 从源码编译

需要 .NET Framework 4.8（或 4.5+）的 csc.exe，本机自带即可；双击 `build.bat`，exe 在 `bin\Muse.exe`。
编译依赖（放 `lib/`）：WebView2 1.0.3595.46、Newtonsoft.Json 13.0.3、YamlDotNet 12.3.1。
