#target photoshop
(function () {
    if ($.os.toLowerCase().indexOf('windows') < 0) { alert('This exchange helper currently supports Windows.'); return; }
    if (!app.documents.length) { alert('Open an image in Photoshop first.'); return; }
    var helper = new File(new File($.fileName).parent.fsName + '/Exchange.ps1');
    if (!helper.exists) { alert('Keep Exchange.ps1 beside Send-to-Composa.jsx.'); return; }
    var source = app.activeDocument;
    var copy = null;
    var image = new File(Folder.temp.fsName + '/Photoshop-to-Composa-' + new Date().getTime() + '-' + Math.floor(Math.random() * 1000000) + '.png');
    var report = new File(image.fsName + '.txt');
    try {
        // Snapshot a duplicate: the original document, layers and save state remain intact.
        copy = source.duplicate('Composa exchange', true);
        if (copy.mode !== DocumentMode.RGB) { copy.changeMode(ChangeMode.RGB); }
        copy.bitsPerChannel = BitsPerChannelType.EIGHT;
        var options = new PNGSaveOptions(); options.interlaced = false;
        copy.saveAs(image, options, true, Extension.LOWERCASE);
        copy.close(SaveOptions.DONOTSAVECHANGES); copy = null; app.activeDocument = source;
        var command = 'powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + helper.fsName + '" -Direction ToComposa -ImagePath "' + image.fsName + '" -ReportPath "' + report.fsName + '"';
        app.system(command);
        // app.system does not capture stdout. Read the helper's explicit result file.
        var deadline = new Date().getTime() + 65000;
        while (!report.exists && new Date().getTime() < deadline) { $.sleep(100); }
        if (!report.exists) { throw new Error('No response from the exchange helper.'); }
        report.encoding = 'UTF-8'; report.open('r'); var result = report.read(); report.close(); report.remove();
        if (result.indexOf('ERROR:') === 0) { alert(result); }
    } catch (error) { alert('Send to Composa failed: ' + error.message); }
    finally { if (copy) { copy.close(SaveOptions.DONOTSAVECHANGES); } app.activeDocument = source; }
}());
