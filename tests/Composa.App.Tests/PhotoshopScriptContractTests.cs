using System.Text;
using System.Text.RegularExpressions;
using Jint;

namespace Composa.App.Tests;

public class PhotoshopScriptContractTests
{
    [Theory]
    [InlineData("none", 8)]
    [InlineData("none", 16)]
    [InlineData("helper", 16)]
    [InlineData("save", 16)]
    [InlineData("close", 16)]
    [InlineData("restore", 16)]
    [InlineData("unsupported", 32)]
    public void Photoshop_script_snapshots_layers_and_restores_the_source_before_handoff(string failure, int depth)
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.js"));
        var jsx = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.jsx"));
        Assert.Equal(script.Replace("Send-to-Composa.js", "Send-to-Composa.jsx"), jsx);
        var engine = new Engine();
        engine.SetValue("failure", failure);
        engine.SetValue("depth", depth);
        engine.Execute("""
            const files = {}, alerts = [];
            let merged, saved = false, closed = false, command = '', folderCount = 0;
            const install = "C:/Тест %20 с пробелами и O'Brien/scripts/photoshop";
            function File(uri) {
                const path = decodeURIComponent(uri);
                if (files[path]) return files[path];
                this.fsName = path;
                this.parent = { absoluteURI: encodeURI(install) };
                this.exists = path.endsWith('Exchange.ps1');
                files[path] = this;
                this.open = mode => mode === 'w' || this.exists;
                this.writeln = value => { command = value; this.exists = true; return true; };
                this.read = () => 'Accepted Photoshop transfer';
                this.close = () => {};
                this.remove = () => { this.exists = false; };
                this.execute = () => {
                    if (!closed || app.activeDocument !== original || app.displayDialogs !== 1 || app.preferences.maximizeCompatibility !== 2)
                        throw new Error('Source state was not restored before running the helper');
                    if (failure === 'helper') return false;
                    Object.keys(files).filter(path => path.endsWith('/result.txt')).forEach(path => files[path].exists = true);
                    return true;
                };
            }
            function Folder(uri) {
                this.absoluteURI = uri;
                this.exists = false;
                this.create = () => { folderCount++; return true; };
                this.remove = () => {};
            }
            Folder.temp = { absoluteURI: encodeURI("C:/Временная папка 'тест'") };
            const $ = {os:'Windows', fileName:encodeURI(install + '/Send-to-Composa.js'),
                sleep: () => { throw new Error('Unexpected polling'); }};
            const DocumentMode = {RGB:1}, BitsPerChannelType = {EIGHT:8, SIXTEEN:16},
                SaveOptions = {DONOTSAVECHANGES:0}, Extension = {LOWERCASE:1}, DialogModes = {NO:0},
                QueryStateType = { ALWAYS:1 };
            function PhotoshopSaveOptions() {}
            function alert(text) { alerts.push(text); }
            const copy = {bitsPerChannel:depth,
                saveAs: (file, options, asCopy, extension) => {
                    if (failure === 'save') throw new Error('Save failed');
                    saved = asCopy && options.layers && options.alphaChannels && options.embedColorProfile &&
                        extension === Extension.LOWERCASE && app.preferences.maximizeCompatibility === QueryStateType.ALWAYS;
                    file.exists = true;
                },
                close: mode => {
                    if (failure === 'close') throw new Error('Close failed');
                    closed = mode === SaveOptions.DONOTSAVECHANGES;
                }};
            // No fullName/path: the active document has never been saved.
            const original = { name: 'Пробный кадр', mode: 1, bitsPerChannel:depth,
                duplicate: (name, merge) => { merged = merge; app.activeDocument = copy; return copy; }};
            const app = {documents:[original], activeDocument:original, displayDialogs:1, preferences:{maximizeCompatibility:2}};
            if (failure === 'restore') Object.defineProperty(app, 'activeDocument', {
                get: () => original, set: value => { if (value === original) throw new Error('Document no longer available'); }});
            """);
        engine.Execute(Regex.Replace(script, @"(?m)^#target.*$", ""));
        Assert.True(engine.Evaluate("app.activeDocument === original && original.bitsPerChannel === depth && app.displayDialogs === 1 && app.preferences.maximizeCompatibility === 2").AsBoolean());
        Assert.Equal(failure == "none" ? 0 : 1, engine.Evaluate("alerts.length").AsNumber());
        if (failure == "unsupported")
        {
            Assert.Equal(0, engine.Evaluate("folderCount").AsNumber());
            return;
        }
        Assert.True(engine.Evaluate("merged === false").AsBoolean());
        Assert.Equal(failure != "save", engine.Evaluate("saved").AsBoolean());
        Assert.Equal(failure != "close", engine.Evaluate("closed").AsBoolean());
        if (failure is "none" or "helper")
        {
            var command = engine.Evaluate("command").AsString();
            var encoded = Regex.Match(command, "-EncodedCommand ([A-Za-z0-9+/=]+)").Groups[1].Value;
            Assert.NotEmpty(encoded);
            var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Assert.StartsWith("& 'C:/Тест %20 с пробелами и O''Brien/scripts/photoshop/Exchange.ps1' -Direction ToComposa", decoded);
            Assert.Contains("-ImagePath 'C:/Временная папка ''тест''/Photoshop-to-Composa-", decoded);
            Assert.Contains("/Photoshop - Пробный кадр.psd' -ReportPath ", decoded);
        }
        else
            Assert.Equal("", engine.Evaluate("command").AsString());
    }
}
