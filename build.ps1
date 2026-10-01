# 使用 Windows 自带的 .NET Framework csc 编译，无需安装任何 SDK
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$gac  = "$env:WINDIR\Microsoft.NET\assembly"

$refs = @(
    "$gac\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    "$gac\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
    "$gac\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$gac\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
) | ForEach-Object { "/reference:$_" }

# 精灵表与音效（wav/mp3）均以清单资源嵌入 exe（逻辑名无空格）
$resources = @(
    "/resource:$root\src\gfx\character_001_isaac.png,character_001_isaac.png",
    "/resource:$root\src\sfx\hurt1.wav,hurt1.wav",
    "/resource:$root\src\sfx\hurt2.wav,hurt2.wav",
    "/resource:$root\src\sfx\hurt3.wav,hurt3.wav",
    "/resource:$root\src\sfx\mom1.mp3,mom1.mp3",
    "/resource:$root\src\sfx\mom2.mp3,mom2.mp3",
    "/resource:$root\src\sfx\mom3.mp3,mom3.mp3",
    "/resource:$root\src\sfx\mouse1.mp3,mouse1.mp3",
    "/resource:$root\src\sfx\mouse2.mp3,mouse2.mp3",
    "/resource:$root\src\sfx\up.mp3,up.mp3",
    "/resource:$root\src\sfx\thumbs up.mp3,thumbs up.mp3",
    "/resource:$root\src\sfx\thumbs down.mp3,thumbs down.mp3",
    "/resource:$root\src\sfx\evil laugh.mp3,evil laugh.mp3"
)

& $csc /nologo /target:winexe /out:"$root\issac.exe" $resources $refs "$root\issac.cs"
