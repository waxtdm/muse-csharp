Muse 一键安装程序（InstallerForm.cs，WinForms，C# 5 写法）
============================================================

作用：用户双击 Muse安装.exe，选个文件夹，点“开始安装”，自动完成：
解压内嵌的完整程序包 -> 跑 build.bat 编译 -> 桌面建 Muse.lnk 快捷方式 -> 启动 Muse。

重新打包安装器（在本机）：
1. 先把当前整棵树打成 payload.zip（内容与 Muse_CSharp版vX.zip 相同：src/ + build.bat
   + Muse.csproj + README_说明.txt + lib/ + res/，用 Python zipfile 打，中文名带 UTF-8 标记）。
2. 编译（mono mcs，与主程序同一套引用）：
   mcs -nologo -target:winexe -out:Muse安装.exe -langversion:5 -optimize -win32icon:res/muse.ico \
     -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
     -resource:payload.zip,MuseSetup.payload.zip installer/InstallerForm.cs
3. 编出的 Muse安装.exe 约 28MB（内嵌的 payload 占大头），直接交付即可。

注意：
- 安装器不参与自动更新：自动更新仍走主程序的 UpdateCheck（下载源码包、本地重编译）。
- 桌面快捷方式用 WScript.Shell 晚期绑定创建；创建失败只记日志、不影响安装（可手动右键 exe 发送到桌面）。
- 编译失败时安装器会停下并显示 build.log 前 600 字，旧的安装不动。
