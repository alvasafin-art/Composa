using System.Text.RegularExpressions;
using Jint;

namespace Composa.App.Tests;

public class PhotoshopScriptContractTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("helper")]
    [InlineData("save")]
    [InlineData("close")]
    [InlineData("restore")]
    public void Photoshop_script_restores_dialogs_even_when_export_or_cleanup_fails(string failure)
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.js"));
        var jsx = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Send-to-Composa.jsx"));
        Assert.Equal(script.Replace("Send-to-Composa.js", "Send-to-Composa.jsx"), jsx);
        var engine = new Engine(); engine.SetValue("failure", failure);
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
                saveAs: (file, options, asCopy, extension) => { if(failure === 'save') throw new Error('Save failed');
                    saved = asCopy && !options.interlaced && extension === Extension.LOWERCASE; file.exists = true; },
                close: () => { if(failure === 'close') throw new Error('Close failed'); closed = true; }};
            const original = {mode:2, bitsPerChannel:16, duplicate: (name, merge) => { merged = merge; return copy; }};
            const app = {documents:[original], activeDocument:original, displayDialogs:1,
                system: value => { if(app.displayDialogs !== 1) throw new Error('Dialogs disabled while waiting for helper');
                    command = value; if(failure === 'helper') throw new Error('Exchange failed');
                    Object.keys(files).filter(path => path.endsWith('.txt')).forEach(path => files[path].exists = true); }};
            if(failure === 'restore') Object.defineProperty(app, 'activeDocument', {
                get: () => original, set: () => { throw new Error('Document no longer available'); }});
            """);
        var executable=Regex.Replace(script, @"(?m)^#target.*$", "");
        if(failure is "close" or "restore")Assert.Throws<Jint.Runtime.JavaScriptException>(()=>engine.Execute(executable));
        else engine.Execute(executable);
        Assert.True(engine.Evaluate("merged && converted").AsBoolean());
        Assert.Equal(failure!="save",engine.Evaluate("saved").AsBoolean());
        Assert.Equal(failure!="close",engine.Evaluate("closed").AsBoolean());
        Assert.True(engine.Evaluate("app.activeDocument === original && original.mode === 2 && original.bitsPerChannel === 16 && app.displayDialogs === 1").AsBoolean());
        Assert.Equal(failure=="none" ? 0 : 1, engine.Evaluate("alerts.length").AsNumber());
        if(failure is "none" or "helper")
        {
            Assert.Contains("-Direction ToComposa", engine.Evaluate("command").AsString());
            Assert.Contains("\"C:/Folder with spaces/scripts/photoshop/Exchange.ps1\"", engine.Evaluate("command").AsString());
        }
    }
}
