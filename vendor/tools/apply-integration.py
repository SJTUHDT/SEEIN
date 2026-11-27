"""Apply the SEEIN integration to the pinned, otherwise untouched tool sources."""
from pathlib import Path
import json
import shutil
import sys

root = Path(__file__).resolve().parents[2]
pane = root / '.build/deps/Pane-0.1.606'
snow = root / '.build/deps/snow-shot-1.6.3'
process_names = json.loads((root / 'config/process-names.json').read_text(encoding='utf-8-sig'))

def copy_source(source, destination):
    data = source.read_bytes()
    if not destination.exists() or destination.read_bytes() != data:
        destination.write_bytes(data)

def write_if_changed(path, text, encoding='utf-8'):
    data = text.encode(encoding)
    if not path.exists() or path.read_bytes() != data:
        path.write_bytes(data)

def replace(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    if new in text:
        return
    if old not in text:
        raise RuntimeError(f'Pinned source context changed: {path}: {old[:70]}')
    write_if_changed(path, text.replace(old, new, 1), encoding='utf-8-sig' if path.suffix == '.ps1' else 'utf-8')

copy_source(root / 'vendor/tools/Pane.Seein.cs', pane / 'src/Pane.App/App.Seein.cs')
copy_source(root / 'vendor/tools/Pane.SearchAlignment.cs', pane / 'src/Pane.App/Hosting/SeeinSearchAlignment.cs')
copy_source(root / 'vendor/tools/Pane.SearchSites.cs', pane / 'src/Pane.App/SeeinSearchSites.cs')
launcher_view = pane / 'src/Pane.App/Views/LauncherView.xaml.cs'
replace(launcher_view, '''        int space = q.IndexOf(' ');
        if (space <= 0) return null;
        var text = q[(space + 1)..].Trim();
        if (text.Length == 0) return null;
        return Options.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, q[..space], StringComparison.OrdinalIgnoreCase)) is { } engine ? (engine, text) : null;''',
        '''        var engine = Pane.Services.Launcher.SearchSites.Parse(Options, q, out var text);
        return engine is not null && text.Length > 0 ? (engine, text) : null;''')
replace(launcher_view, '''        var s = raw.TrimStart();
        int space = s.IndexOf(' ');
        if (space <= 0) return null;
        return Options.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, s[..space], StringComparison.OrdinalIgnoreCase));''',
        '''        return Pane.Services.Launcher.SearchSites.Parse(Options, raw, out _);''')
replace(launcher_view, '            var s = Section(only.Name, LauncherSectionKind.List);',
        '            var s = Section(only.Name, LauncherSectionKind.List);\n            Add(s, SeeinSiteChoice(only), LauncherCell.Command);')
replace(launcher_view, '            var apps = result.Items.Where(i => i.Kind is LauncherItemKind.App',
        '''            foreach (var site in Pane.Services.Launcher.SearchSites.Matches(Options, q))
                Add(Section("搜索站点", LauncherSectionKind.List), SeeinSiteChoice(site), LauncherCell.Command);
            var apps = result.Items.Where(i => i.Kind is LauncherItemKind.App''')
replace(launcher_view, '        var q = shownQuery;\n        if (row.Cell == LauncherCell.Menu)',
        '''        var q = shownQuery;
        if (item.Id.StartsWith("site:", StringComparison.Ordinal))
        {
            Fill(item.Id[5..] + " ");
            Input.Focus(FocusState.Programmatic);
            return;
        }
        if (row.Cell == LauncherCell.Menu)''')
replace(launcher_view, '            LauncherCell.Command when item.Id == "web:hint"',
        '            LauncherCell.Command when item.Id.StartsWith("site:", StringComparison.Ordinal) => (item.Subtitle ?? item.Title, [(["Enter"], "选择站点")]),\n            LauncherCell.Command when item.Id == "web:hint"')
replace(pane / 'src/Pane.Native/Launcher.cpp',
        'namespace {\nint launcherOpen(const wchar_t* target,int packaged){',
        'extern "C" HRESULT __stdcall LaunchPaneFromDesktop(const wchar_t*,const wchar_t*,const wchar_t*,BOOL) noexcept;\nnamespace {\nint launcherOpen(const wchar_t* target,int packaged){')
replace(pane / 'src/Pane.Native/Launcher.cpp',
        ' // 文件属性查询也在受限工作线程内，网络路径不可达时不会占住调用者。',
        ' if(GetEnvironmentVariableW(L"SEEIN_TOOL_PIPE",nullptr,0))return SUCCEEDED(LaunchPaneFromDesktop(target,L"",L"",TRUE));\n // 文件属性查询也在受限工作线程内，网络路径不可达时不会占住调用者。')
replace(pane / 'src/Pane.App/Services/SelectionService.cs',
        'Process.Start(new ProcessStartInfo("https://www.bing.com/search?q="+Uri.EscapeDataString(text)){UseShellExecute=true})?.Dispose();',
        'if(App.SeeinMode)App.SeeinOpenExternal("https://www.bing.com/search?q="+Uri.EscapeDataString(text));else Process.Start(new ProcessStartInfo("https://www.bing.com/search?q="+Uri.EscapeDataString(text)){UseShellExecute=true})?.Dispose();')
replace(pane / 'src/Pane.App/Pane.App.csproj',
        '<OutputType>WinExe</OutputType>',
        '<OutputType>WinExe</OutputType><AssemblyName>seewin</AssemblyName><AssemblyTitle>seewin</AssemblyTitle><FileDescription>seewin · SEEIN 效率工具</FileDescription>')
replace(pane / 'src/Pane.Native/Launcher.cpp',
        'HWND endpoint(){HWND h=FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION",nullptr);',
        'HWND endpoint(){wchar_t instance[128]{};GetEnvironmentVariableW(L"SEEIN_INDEX_INSTANCE",instance,128);std::wstring name=L"EVERYTHING_TASKBAR_NOTIFICATION_(";name+=instance[0]?instance:L"SEEIN";name+=L")";if(HWND owned=FindWindowW(name.c_str(),nullptr))return owned;if(GetEnvironmentVariableW(L"SEEIN_TOOL_PIPE",nullptr,0))return nullptr;HWND h=FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION",nullptr);')
replace(pane / 'src/Pane.Native/Launcher.cpp',
        'if(HWND owned=FindWindowW(name.c_str(),nullptr))return owned;HWND h=',
        'if(HWND owned=FindWindowW(name.c_str(),nullptr))return owned;if(GetEnvironmentVariableW(L"SEEIN_TOOL_PIPE",nullptr,0))return nullptr;HWND h=')
replace(pane / 'src/Pane.App/App.Secondary.cs',
        'public void ShowFiles(string query,string? focus)=>Present(SecondaryId.Files, Strings.Common_Files,()=>new FilesView(Files,secondaryActions!),v=>((FilesView)v).Open(query,focus));',
        '''public void ShowFiles(string query,string? focus)
    {
        Windows.Foundation.Point? center=null;
        if(SeeinMode&&panels.TryGetValue("launcher",out var launcher)&&launcher.Shown&&launcher.View is LauncherView source)
            center=launcher.SeeinSearchCenter(source.SeeinSearchInput);
        Present(SecondaryId.Files, Strings.Common_Files,()=>new FilesView(Files,secondaryActions!),v=>((FilesView)v).Open(query,focus),searchCenter:center);
    }''')
replace(pane / 'src/Pane.App/App.Secondary.cs',
        'Action<IPanelView> open,(int X,int Y)? at=null)',
        'Action<IPanelView> open,(int X,int Y)? at=null,Windows.Foundation.Point? searchCenter=null)')
replace(pane / 'src/Pane.App/App.Secondary.cs',
        '        // 从启动器 / 超级面板展开：那两个面板先收起',
        '''        if(searchCenter is {} center&&panel.View is FilesView files)
            panel.SeeinAlignSearchAt(files.SeeinSearchInput,center);
        // 从启动器 / 超级面板展开：那两个面板先收起''')
replace(pane / 'src/Pane.App/App.Secondary.cs',
        '        panel.BringToFront(activate:!demo&&Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");',
        '''        panel.BringToFront(activate:!demo&&Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");
        // Restoring a minimized window can restore its OS-saved bounds.
        if(searchCenter is {} restoredCenter&&panel.View is FilesView restoredFiles)
            panel.SeeinAlignSearchAt(restoredFiles.SeeinSearchInput,restoredCenter);''')
replace(pane / 'src/Pane.App/Services/Launcher/LauncherPlatform.cs',
        '            return new LauncherFileRows(page.Items.Select(hit=>',
        '            Set(FileSearchState.Ready,Version);\n            return new LauncherFileRows(page.Items.Select(hit=>')
replace(snow / 'snow_shot/src/storage/storageusagetracker.cpp',
        'QString defaultCacheDirectory() {',
        'QString defaultCacheDirectory() {\n    const QString seeinCache = qEnvironmentVariable("SEEIN_CACHE_DIRECTORY");\n    if (!seeinCache.isEmpty()) return QDir::cleanPath(seeinCache);')
replace(snow / 'snow-crates/crates/snow-recording-runtime/src/deferred.rs',
        '    let parent = options.working_directory.unwrap_or_else(|| {',
        '    let parent = options.working_directory.or_else(|| std::env::var_os("SEEIN_RECORDING_STAGING_DIRECTORY").filter(|value| !value.is_empty()).map(PathBuf::from)).unwrap_or_else(|| {')
recording_export = snow / 'snow-crates/crates/snow-recording-export/src/streaming.rs'
replace(recording_export,
        '    let directory = final_path\n        .parent()\n        .filter(|path| !path.as_os_str().is_empty())\n        .unwrap_or_else(|| Path::new("."));\n    let extension = final_path',
        '    let seein_staging = std::env::var_os("SEEIN_RECORDING_STAGING_DIRECTORY").filter(|value| !value.is_empty()).map(PathBuf::from);\n    let directory = seein_staging.as_deref().unwrap_or_else(|| final_path\n        .parent()\n        .filter(|path| !path.as_os_str().is_empty())\n        .unwrap_or_else(|| Path::new(".")));\n    fs::create_dir_all(directory)?;\n    let extension = final_path')
replace(recording_export,
        'fn publish_staging_file(staging_path: &Path, final_path: &Path) -> std::io::Result<()> {',
        'fn publish_staging_file(staging_path: &Path, final_path: &Path) -> std::io::Result<()> {\n    if std::env::var_os("SEEIN_RECORDING_STAGING_DIRECTORY").is_some() {\n        let directory = final_path.parent().filter(|path| !path.as_os_str().is_empty()).unwrap_or_else(|| Path::new("."));\n        fs::create_dir_all(directory)?;\n        let mut published = tempfile::Builder::new().prefix(".seein-final-recording-").tempfile_in(directory)?;\n        let mut source = std::fs::File::open(staging_path)?;\n        std::io::copy(&mut source, published.as_file_mut())?;\n        published.as_file().sync_all()?;\n        published.persist(final_path).map_err(|failure| failure.error)?;\n        drop(source);\n        let _ = fs::remove_file(staging_path);\n        return Ok(());\n    }')
replace(snow / 'snow_shot/src/presentation/collage/collagewindow.cpp',
        'dialog->setDirectory(storage::ScreenshotSettings().lastManualSaveDirectory());',
        'const QString seeinPdfDirectory = qEnvironmentVariable("SEEIN_PDF_DIRECTORY");\n        dialog->setDirectory(seeinPdfDirectory.isEmpty() ? storage::ScreenshotSettings().lastManualSaveDirectory() : seeinPdfDirectory);')
replace(pane / 'src/Pane.App/Pane.App.csproj',
        '<Target Name="BuildNativeEffect" BeforeTargets="PrepareForBuild">',
        '<Target Name="BuildNativeEffect" BeforeTargets="PrepareForBuild" Condition="\'$(SkipNativeBuild)\' != \'true\'">')
replace(pane / 'src/Pane.App/App.WinV.cs', 'if (!IsProduction || demo || shell == null) return;',
        'if (SeeinMode || !IsProduction || demo || shell == null) return;')
native = pane / 'src/Pane.App/scripts/native.ps1'
native_text = native.read_text(encoding='utf-8-sig').replace('/std:c++17', '/std:c++20')
write_if_changed(native, native_text, encoding='utf-8-sig')
replace(pane / 'src/Pane.App/scripts/native.ps1',
        ". (Join-Path $root '../../scripts/find-build-tools.ps1')",
        "if (!$env:SEEIN_PORTABLE_MSVC) {\n. (Join-Path $root '../../scripts/find-build-tools.ps1')")
replace(pane / 'src/Pane.App/scripts/native.ps1',
        "$native=Join-Path $root 'bin/native'", "}\n$native=Join-Path $root 'bin/native'")
replace(pane / 'src/Pane.App/App.xaml.cs', 'Store.Load();',
        'Store.Load(); if(SeeinMode) Store.Save(SeeinSanitize(Store.Current));')
replace(pane / 'src/Pane.App/App.xaml.cs', 'await StartModules();',
        'await StartModules(); StartSeeinBridge();')
replace(pane / 'src/Pane.App/App.xaml.cs', 'if(IsProduction&&!demo)Updates=',
        'if(IsProduction&&!demo&&!SeeinMode)Updates=')
replace(pane / 'src/Pane.App/App.xaml.cs', 'if(command=="show-settings"){',
        'if(command=="show-settings"&&SeeinMode){OpenSeeinSettings();return;}\n        if(command=="show-settings"){')
replace(pane / 'src/Pane.App/App.Secondary.cs',
        'public void OpenSettings(string? section){',
        'public void OpenSettings(string? section){if(SeeinMode){OpenSeeinSettings();return;}')
replace(pane / 'src/Pane.App/Views/LauncherView.xaml.cs', 'void Fill(string text)',
        'public void SeeinFill(string text) => Fill(text);\n    void Fill(string text)')
replace(pane / 'src/Pane.App/Services/FolderJumpService.cs', 'void OnTrigger(nint dialog, int command, int x, int y)',
        'public void SeeinInvoke(bool menu) => OnTrigger(GetForegroundWindow(),menu?2:1,0,0);\n    void OnTrigger(nint dialog, int command, int x, int y)')
replace(pane / 'src/Pane.App/Hosting/ShellHost.cs', 'void Register(int id,string name,Hotkey key){',
        'void Register(int id,string name,Hotkey key){\n        if(App.SeeinMode)return;')
replace(pane / 'src/Pane.App/Services/FolderJumpService.cs',
        'Modifiers(value.Jump), value.Jump.VirtualKey,',
        'App.SeeinMode?0:Modifiers(value.Jump), App.SeeinMode?(ushort)0:value.Jump.VirtualKey,')
replace(pane / 'src/Pane.App/Services/FolderJumpService.cs',
        'Modifiers(value.Menu), value.Menu.VirtualKey,',
        'App.SeeinMode?0:Modifiers(value.Menu), App.SeeinMode?(ushort)0:value.Menu.VirtualKey,')
print('Pane integration applied to pinned source')

if '--pane-only' in sys.argv:
    sys.exit(0)
replace(snow / 'snow_shot/CMakeLists.txt',
        'snow_shot_link_macos_window_platform(snow_shot)',
        'if(WIN32)\n    set_target_properties(snow_shot PROPERTIES OUTPUT_NAME seeshot)\nendif()\ntarget_sources(snow_shot PRIVATE src/app/seeinexternal.cpp)\nsnow_shot_link_macos_window_platform(snow_shot)')
replace(snow / 'snow_shot/resources/windows-app-resource.rc.in',
        'VALUE "FileDescription", "@SNOW_SHOT_PRODUCT_NAME@ screenshot utility\\0"',
        'VALUE "FileDescription", "seeshot - SEEIN screenshot and recording\\0"')
replace(snow / 'snow_shot/resources/windows-app-resource.rc.in',
        'VALUE "OriginalFilename", "@SNOW_SHOT_EXECUTABLE_NAME@.exe\\0"',
        'VALUE "OriginalFilename", "seeshot.exe\\0"')
replace(snow / 'snow_shot/CMakeLists.txt',
        '"$<TARGET_FILE_DIR:snow_shot>/crashpad_handler.exe"',
        '"$<TARGET_FILE_DIR:snow_shot>/seecrash.exe"')
replace(snow / 'snow_shot/CMakeLists.txt',
        'install(PROGRAMS "${SNOW_CRASHPAD_HANDLER}" DESTINATION bin)',
        'install(PROGRAMS "${SNOW_CRASHPAD_HANDLER}" DESTINATION bin RENAME seecrash.exe)')
replace(snow / 'snow_shot/src/app/main.cpp',
        'QStringLiteral("crashpad_handler.exe")', 'QStringLiteral("seecrash.exe")')
replace(snow / 'snow_shot/include/snow_shot/app/edition.h',
        '    return applicationName() + QStringLiteral(".exe");',
        '    if (!qEnvironmentVariableIsEmpty("SEEIN_TOOL_PIPE")) return QStringLiteral("seeshot.exe");\n    return applicationName() + QStringLiteral(".exe");')
replace(snow / 'snow_shot/include/snow_shot/app/edition.h',
        '    return productId() + QStringLiteral("-updater.exe");',
        '    if (!qEnvironmentVariableIsEmpty("SEEIN_TOOL_PIPE")) return QStringLiteral("seeupdate.exe");\n    return productId() + QStringLiteral("-updater.exe");')
replace(snow / 'snow_shot/src/presentation/ocr/screenshotocrassets.cpp',
        '    result.processPath = QDir(result.runtimeDirectory).filePath(descriptor.executable);',
        '''    result.processPath = QDir(result.runtimeDirectory).filePath(descriptor.executable);
    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) {
        // Keep upstream asset names/checksums intact. Execute a verified branded
        // copy in the same versioned directory so adjacent runtime DLLs resolve.
        const QString branded = QDir(result.runtimeDirectory).filePath(QStringLiteral("seeocr.exe"));
        const QByteArray expected = sha256File(result.processPath, nullptr);
        if (expected.isEmpty()) { result.processPath.clear(); return result; }
        if (sha256File(branded, nullptr) != expected) {
            if ((QFile::exists(branded) && !QFile::remove(branded)) || !QFile::copy(result.processPath, branded) ||
                sha256File(branded, nullptr) != expected) { result.processPath.clear(); return result; }
        }
        result.processPath = branded;
    }''')
if (root / 'vendor/tools/snow-seein-bridge.inc').exists():
    copy_source(root / 'vendor/tools/snow-seein-bridge.inc', snow / 'snow_shot/src/app/seein_bridge.inc')
    copy_source(root / 'vendor/tools/snow-seein-secret.h', snow / 'snow_shot/include/snow_shot/seeinsecret.h')
    copy_source(root / 'vendor/tools/snow-seein-secret.cpp', snow / 'snow_shot/src/app/seeinsecret.cpp')
    replace(snow / 'snow_shot/CMakeLists.txt', 'set(SNOW_SHOT_STORAGE_SOURCES', 'set_source_files_properties(src/app/seeinsecret.cpp PROPERTIES SKIP_UNITY_BUILD_INCLUSION ON)\nset(SNOW_SHOT_STORAGE_SOURCES\n    src/app/seeinsecret.cpp')
    copy_source(root / 'vendor/tools/snow-seein-shell.h', snow / 'snow_shot/include/snow_shot/seeinshell.h')
    copy_source(root / 'vendor/tools/snow-seein-external.h', snow / 'snow_shot/include/snow_shot/seeinexternal.h')
    copy_source(root / 'vendor/tools/snow-seein-external.cpp', snow / 'snow_shot/src/app/seeinexternal.cpp')
    for source_file in (snow / 'snow_shot/src').rglob('*.cpp'):
        if source_file.name == 'seeinexternal.cpp':
            continue
        text = source_file.read_text(encoding='utf-8-sig')
        if 'QDesktopServices::openUrl' in text:
            text = text.replace('QDesktopServices::openUrl', 'snow_shot::seeinOpenUrl')
            if '#include "snow_shot/seeinexternal.h"' not in text:
                text = '#include "snow_shot/seeinexternal.h"\n' + text
            write_if_changed(source_file, text)
    model_header = snow / 'snow_shot/include/snow_shot/customaimodelconfiguration.h'
    replace(model_header, '#include <QCryptographicHash>', '#include <QCryptographicHash>\n#include "snow_shot/seeinsecret.h"')
    replace(model_header, '{QStringLiteral("api_key"), model.apiKey}', '{QStringLiteral("api_key"), seeinProtectSecret(model.apiKey)}')
    replace(model_header, 'object.value(QStringLiteral("api_key")).toString(),', 'seeinUnprotectSecret(object.value(QStringLiteral("api_key")).toString()),')
    controller = snow / 'snow_shot/src/app/applicationcontroller.cpp'
    replace(controller, '#include "snow_shot/app/applicationcontroller.h"', '#include "snow_shot/app/applicationcontroller.h"\n#include "snow_shot/storage/configurationschema.h"')
    replace(controller, '#include <QApplication>', '#include <QApplication>\n#include <QLocalServer>\n#include <QLocalSocket>\n#include <QJsonDocument>\n#include <QFileDialog>\n#include <QJsonArray>\n#include <QTimer>\n#include "snow_shot/presentation/settings/settingsruntimesession.h"\n#include "snow_shot/presentation/settings/settingsregistry.h"')
    replace(controller, 'class ApplicationController::Impl {\n  public:',
            'class ApplicationController::Impl {\n  public:\n#include "seein_bridge.inc"')
    window_source = snow / 'snow_shot/src/presentation/mainwindow.cpp'
    replace(window_source, '#include "snow_shot/app/edition.h"', '#include "snow_shot/app/edition.h"\n#include "snow_shot/seeinshell.h"')
    for signature in ('void MainWindow::showScreenshotSettings() {', 'void MainWindow::showSettingsLocation(const QString& pageId, const QString& sectionId) {', 'void MainWindow::showGeneralSettings() {'):
        replace(window_source, signature, signature + '\n    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) { snow_shot::seeinOpenSettings(); return; }')
    screenshot_source = snow / 'snow_shot/src/presentation/services/screenshotcontroller.cpp'
    replace(screenshot_source, '#include "snow_shot/presentation/screenshotcontroller.h"', '#include "snow_shot/presentation/screenshotcontroller.h"\n#include "snow_shot/seeinshell.h"')
    replace(screenshot_source, 'void ScreenshotController::Impl::openTextTranslationSettings() {', 'void ScreenshotController::Impl::openTextTranslationSettings() {\n    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) { snow_shot::seeinOpenSettings(); return; }')
    replace(controller, 'm_impl->start(initialArguments, std::move(maintenance));',
            'm_impl->start(initialArguments, std::move(maintenance));\n    m_impl->startSeeinBridge();')
    replace(controller, '        systemTray.show();\n        globalShortcutManager.initialize();',
            '        if (!seeinMode()) systemTray.show();\n        if (!seeinMode()) globalShortcutManager.initialize();')
    replace(controller, '        globalMouseManager.initialize();', '        if (!seeinMode()) globalMouseManager.initialize();')
    replace(controller, '    void showMainWindow() {',
            '    void showMainWindow() {\n        if (seeinMode()) { openSeeinSettings(); return; }')
    replace(controller, '    void showGeneralSettings() {',
            '    void showGeneralSettings() {\n        if (seeinMode()) { openSeeinSettings(); return; }')
    replace(controller, '    void restorePinnedWindows() {', '    void restorePinnedWindows() {\n        if (seeinMode()) return;')
    # Suppress upstream online update/account maintenance and restoration. SEEIN owns lifecycle.
    replace(controller, '    void finishStartup(std::function<void()> maintenance) {',
            '    void finishStartup(std::function<void()> maintenance) {\n        if (seeinMode()) return;')
    storage = snow / 'snow_shot/src/storage/applicationstorage.cpp'
    replace(storage, 'options.appDataDirectory.isEmpty()',
            'options.appDataDirectory.isEmpty() && qEnvironmentVariable("SEEIN_TOOL_DATA").isEmpty()')
    # Both resolve and initialization need the same managed data root.
    txt = storage.read_text(encoding='utf-8')
    txt = txt.replace(': options.appDataDirectory;', ': (qEnvironmentVariable("SEEIN_TOOL_DATA").isEmpty() ? options.appDataDirectory : qEnvironmentVariable("SEEIN_TOOL_DATA"));')
    write_if_changed(storage, txt)
    txt = storage.read_text(encoding='utf-8').replace('        options.appDataDirectory.isEmpty()\n', '        options.appDataDirectory.isEmpty() && qEnvironmentVariable("SEEIN_TOOL_DATA").isEmpty()\n')
    write_if_changed(storage, txt)
    schema = snow / 'snow_shot/src/storage/configurationschema.cpp'
    for old, new in [
        ('u"capture_history/retention_days", integerDefault(7)', 'u"capture_history/retention_days", integerDefault(30)'),
        ('u"system/auto_start_at_boot", booleanDefault(true)', 'u"system/auto_start_at_boot", booleanDefault(false)'),
        ('u"updates/mode", stringDefault(u"next_launch")', 'u"updates/mode", stringDefault(u"manual")'),
        ('u"system/context_menu_mode", stringDefault(u"image_files")', 'u"system/context_menu_mode", stringDefault(u"none")'),
        ('u"tray/enabled", booleanDefault(true)', 'u"tray/enabled", booleanDefault(false)'),
        ('u"pinned_history/enabled", booleanDefault(true)', 'u"pinned_history/enabled", booleanDefault(false)'),
        ('u"trim",     u"save"', 'u"save"')]:
        replace(schema, old, new)
    edition = snow / 'snow_shot/include/snow_shot/app/editionfeatures.h'
    replace(edition, '[[nodiscard]] inline bool editionActionToolAvailable(const QString& id) {',
        '''[[nodiscard]] inline bool editionActionToolAvailable(const QString& id) {
    const QStringList excluded{QStringLiteral("pin-to-screen"), QStringLiteral("pin-window-to-screen"),
        QStringLiteral("image-stitching"), QStringLiteral("image-splitting"), QStringLiteral("image-compression"),
        QStringLiteral("color-picker"), QStringLiteral("trim"), QStringLiteral("global-canvas"),
        QStringLiteral("angle"), QStringLiteral("distance"), QStringLiteral("cloud-upload")};
    if (excluded.contains(id)) return false;''')
    replace(edition, '#include <QString>', '#include <QString>\n#include <QStringList>')
    # This fork has translation + API but none of the advanced OCR or extended cloud tools.
    editions = snow / 'cmake/SnowShotMini.cmake'
    replace(editions, '    target_compile_definitions(snow_shot_edition_full INTERFACE SNOW_SHOT_ENABLE_${_feature}=1)',
        '''    if(_feature STREQUAL "TEXT_TRANSLATION" OR _feature STREQUAL "API_CONFIGURATION")
        target_compile_definitions(snow_shot_edition_full INTERFACE SNOW_SHOT_ENABLE_${_feature}=1)
    else()
        target_compile_definitions(snow_shot_edition_full INTERFACE SNOW_SHOT_ENABLE_${_feature}=0)
    endif()''')
    palette = snow / 'snow_shot/src/presentation/tools/screenshottoolpalette.cpp'
    replace(palette, 'if (!options.showRecordingControls && (options.actions & PinAction) != 0) {',
            'if (false && !options.showRecordingControls && (options.actions & PinAction) != 0) {')
    # Keep audio/format/quality options, remove webcam, key display and mouse effects.
    txt = palette.read_text(encoding='utf-8')
    for name in ['m_recordWebcamButton', 'm_recordKeyboardButton', 'm_recordMouseButton',
                 'm_recordFollowMouseButton', 'm_recordPostProcessingButton']:
        txt = txt.replace(f'layout->addWidget({name});', f'{name}->hide(); // removed in SEEIN')
    write_if_changed(palette, txt)
    collage = snow / 'snow_shot/src/presentation/collage/collagewindow.cpp'
    replace(collage, 'modal->setWindowTitle(imageStitchingText("Image stitching"));',
            'modal->setWindowTitle(imageStitchingText("PDF documents"));')
    replace(collage, '    const bool firstPresentation = !m_impl->modal->isOpen();',
            '    m_impl->document->setOutputType(ImageStitchingOutputType::PdfDocument);\n    const bool firstPresentation = !m_impl->modal->isOpen();')
    collage_editor = snow / 'snow_shot/src/presentation/collage/collageeditorwidget.cpp'
    replace(collage_editor, '    d.outputTypeField = d.form->addField({}, d.outputType, QStringLiteral("output_type"));',
            '    d.outputTypeField = d.form->addField({}, d.outputType, QStringLiteral("output_type"));\n    d.outputTypeField->hide();')
    window = snow / 'snow_shot/src/presentation/mainwindow.cpp'
    replace(window, '    m_sidebar = sidebar;', '    m_sidebar = sidebar;\n    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) sidebar->hide();')
    replace(window, '    m_contentHeader = contentHeader;', '    m_contentHeader = contentHeader;\n    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) contentHeader->hide();')
    recording = snow / 'snow_shot/src/presentation/recording/screenrecordingcontroller.cpp'
    replace(recording, '#include "snow_shot/platform/screenshotwindownames.h"',
            '#include "snow_shot/platform/screenshotwindownames.h"\n#include "snow_shot/seeinshell.h"')
    replace(recording, '            uiSession->connections.get(), [this] {\n                if (sessionStatus.state() != ScreenshotToolPalette::RecordingState::Idle',
            '            uiSession->connections.get(), [this] {\n                if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) { snow_shot::seeinOpenSettings(); return; }\n                if (sessionStatus.state() != ScreenshotToolPalette::RecordingState::Idle')
    instance = snow / 'snow_shot/src/app/singleinstancecoordinator.cpp'
    replace(instance, '    const QByteArray identity = identityText.toUtf8();',
            '    identityText += u\'|\' + qEnvironmentVariable("SEEIN_TOOL_PIPE");\n    const QByteArray identity = identityText.toUtf8();')
    history = snow / 'snow_shot/src/presentation/components/screenshothistorypagewidget.cpp'
    for assignment, target in [('m_pinAction = pin;', 'pin'), ('m_stitchAction = stitch;', 'stitch'),
                               ('m_splitAction = split;', 'split')]:
        replace(history, '        ' + assignment, '        ' + assignment + '\n        ' + target + '->setVisible(false);')
    replace(history, '        m_imageCompressionAction->setEnabled(m_record.result.has_value());',
            '        m_imageCompressionAction->setEnabled(false);\n        m_imageCompressionAction->setVisible(false);')
    # Batch PDF export is provided by the dedicated PDF window; remove legacy stitch/pin menu.
    replace(history, '    m_batchActionsButton->setText(tr("Batch actions"));',
            '    m_batchActionsButton->setText(tr("Batch actions"));\n    m_batchActionsButton->hide();')
    # Pins also have keyboard/direct capture entry points; guard those at the controller boundary.
    capture = snow / 'snow_shot/src/presentation/services/screenshotcontroller.cpp'
    for signature in ['void ScreenshotController::captureAndPinSelection() {',
                      'void ScreenshotController::pinClipboardContentToScreen() {',
                      'void ScreenshotController::pinSelectedFilesToScreen() {']:
        replace(capture, signature, signature + '\n    if (!qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) return;')
    # Hidden toolbar controls are not materialized by these settings fields.
    txt = palette.read_text(encoding='utf-8')
    txt = txt.replace('    if (itemId == QStringLiteral("trim"))\n        return m_recordTrimButton;',
                      '    if (itemId == QStringLiteral("trim"))\n        return nullptr;')
    write_if_changed(palette, txt)
    replace(palette, '    connect(m_recordOutputFormatSelect, &adqt::widgets::AdSelect::currentValueChanged, this,',
        '''    for (QWidget* removed : {static_cast<QWidget*>(m_recordMouseTrailIcon),
            static_cast<QWidget*>(m_recordMouseClickIcon), static_cast<QWidget*>(m_recordMouseTrailColorPicker),
            static_cast<QWidget*>(m_recordMouseClickColorPicker), static_cast<QWidget*>(m_recordSettingsButton),
            static_cast<QWidget*>(m_recordKeyboardButton), static_cast<QWidget*>(m_recordWebcamButton),
            static_cast<QWidget*>(m_recordPostProcessingButton)}) if (removed) removed->hide();
    for (auto* button : m_recordMouseTrailColorPresets->buttons()) button->hide();
    for (auto* button : m_recordMouseClickColorPresets->buttons()) button->hide();
    for (const auto* id : {"screenRecordingExportTrailSeparator", "screenRecordingExportClickSeparator"})
        if (auto* separator = findChild<QWidget*>(QString::fromLatin1(id))) separator->hide();
    connect(m_recordOutputFormatSelect, &adqt::widgets::AdSelect::currentValueChanged, this,''')
    replace(capture, 'bool ScreenshotController::captureForAction(CaptureAction action) {',
            'bool ScreenshotController::captureForAction(CaptureAction action) {\n    if (action == CaptureAction::Pin) return false;')
print('Snow Shot integration applied to pinned source')
