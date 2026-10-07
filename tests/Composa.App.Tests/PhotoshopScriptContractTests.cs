using System.Text.RegularExpressions;
using Jint;

namespace Composa.App.Tests;

public class PhotoshopScriptContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Photoshop_script_exports_a_merged_copy_and_restores_document_and_dialogs(bool failCommand)
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.js"));
        var jsx = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.jsx"));
        Assert.Equal(script.Replace("Send-to-Composa.js", "Send-to-Composa.jsx"), jsx);
        var engine = new Engine(); engine.SetValue("failCommand", failCommand);
        engine.Execute("""
            const files = {}, alerts = []; let merged = false, saved = false, closed = false, converted = false, command = '';
            function File(path) {
                if(files[path]) return files[path];
                this.fsName = path; this.parent = { fsName: 'C:/Folder with spaces/scripts/photoshop' };
                this.exists = path.endsWith('Exchange.ps1'); files[path] = this;
                this.open = () => true; this.read = () => 'Opened image in Composa'; this.close = () => {};
                this.remove = () => { this.exists = false; };
            }
            const Folder = {temp: {fsName: 'C:/Temp folder'}}, $ = {os:'Windows', fileName:'C:/Folder with spaces/scripts/photoshop/Send-to-Composa.js',
                sleep: () => { throw new Error('Unexpected polling'); }};
            const DocumentMode = {RGB:1, CMYK:2}, ChangeMode = {RGB:1}, BitsPerChannelType = {EIGHT:8},
                SaveOptions = {DONOTSAVECHANGES:0}, Extension = {LOWERCASE:1}, DialogModes = {NO:0};
            function PNGSaveOptions() { this.interlaced = true; }
            function alert(text) { alerts.push(text); }
            const copy = {mode:2, bitsPerChannel:16, changeMode: v => { converted = v === ChangeMode.RGB; },
                saveAs: (file, options, asCopy, extension) => { saved = asCopy && !options.interlaced && extension === Extension.LOWERCASE; file.exists = true; },
                close: () => { closed = true; }};
            const original = {mode:2, bitsPerChannel:16, duplicate: (name, merge) => { merged = merge; return copy; }};
            const app = {documents:[original], activeDocument:original, displayDialogs:1,
                system: value => { command = value; if(failCommand) throw new Error('Exchange failed');
                    Object.keys(files).filter(path => path.endsWith('.txt')).forEach(path => files[path].exists = true); }};
            """);
        engine.Execute(Regex.Replace(script, @"(?m)^#target.*$", ""));
        Assert.True(engine.Evaluate("merged && saved && closed && converted").AsBoolean());
        Assert.True(engine.Evaluate("app.activeDocument === original && original.mode === 2 && original.bitsPerChannel === 16 && app.displayDialogs === 1").AsBoolean());
        Assert.Equal(failCommand ? 1 : 0, engine.Evaluate("alerts.length").AsNumber());
        Assert.Contains("-Direction ToComposa", engine.Evaluate("command").AsString());
        Assert.Contains("\"C:/Folder with spaces/scripts/photoshop/Exchange.ps1\"", engine.Evaluate("command").AsString());
    }
}
