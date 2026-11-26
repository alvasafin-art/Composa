#target photoshop
(function () {
    if ($.os.toLowerCase().indexOf('windows') < 0) { alert('This exchange helper currently supports Windows.'); return; }
    if (!app.documents.length) { alert('Open a document in Photoshop first.'); return; }
    var helper = new File(new File($.fileName).parent.absoluteURI + '/Exchange.ps1');
    if (!helper.exists) { alert('Keep Exchange.ps1 beside Send-layers-to-Composa.js in the Composa installation.'); return; }

    var source = app.activeDocument;
    if (source.mode !== DocumentMode.RGB ||
        (source.bitsPerChannel !== BitsPerChannelType.EIGHT && source.bitsPerChannel !== BitsPerChannelType.SIXTEEN)) {
        alert('Composa imports RGB Photoshop files at 8 or 16 bits per channel. Convert a copy in Photoshop, then send it.');
        return;
    }

    var folder = new Folder(Folder.temp.absoluteURI + '/Photoshop-to-Composa-' + new Date().getTime() + '-' + Math.floor(Math.random() * 1000000));
    if (folder.exists || !folder.create()) { alert('Could not create a temporary folder for the PSD.'); return; }
    var name = source.name.replace(/\.[^.]+$/, '').replace(/[<>:"\/\\|?*\x00-\x1f]/g, '_').replace(/[. ]+$/, '');
    var image = new File(folder.absoluteURI + '/' + encodeURIComponent('Photoshop - ' + (name || 'Untitled').substring(0, 100) + '.psd'));
    var report = new File(folder.absoluteURI + '/result.txt');
    var launcher = new File(folder.absoluteURI + '/launch.vbs');
    var copy = null;
    var submitted = false;
    try {
        var dialogs = app.displayDialogs;
        var compatibility = app.preferences.maximizeCompatibility;
        try {
            app.displayDialogs = DialogModes.NO;
            app.preferences.maximizeCompatibility = QueryStateType.ALWAYS;
            copy = source.duplicate('Composa exchange', false);
            var options = new PhotoshopSaveOptions();
            options.layers = true;
            options.alphaChannels = true;
            options.embedColorProfile = true;
            copy.saveAs(image, options, true, Extension.LOWERCASE);
        } finally {
            try { if (copy) { copy.close(SaveOptions.DONOTSAVECHANGES); } }
            finally {
                try { app.activeDocument = source; }
                finally {
                    try { app.preferences.maximizeCompatibility = compatibility; }
                    finally { app.displayDialogs = dialogs; }
                }
            }
        }

        // Keep Windows command-shell parsing away from the paths (including % and Unicode).
        var command = '& ' + psLiteral(helper.fsName) + ' -Direction ToComposa -ImagePath ' +
            psLiteral(image.fsName) + ' -ReportPath ' + psLiteral(report.fsName);
        // Photoshop's ExtendScript can open a file through Windows but has no portable shell API.
        launcher.encoding = 'ASCII';
        if (!launcher.open('w')) { throw new Error('Could not create the exchange launcher.'); }
        try {
            if (!launcher.writeln('CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand ' +
                encodeCommand(command) + '", 0, False')) { throw new Error('Could not write the exchange launcher.'); }
        } finally { launcher.close(); }
        if (!launcher.execute()) { throw new Error('Windows Script Host could not start the exchange launcher.'); }
        submitted = true;
        var deadline = new Date().getTime() + 10000;
        while (!report.exists && new Date().getTime() < deadline) { $.sleep(100); }
        report.encoding = 'UTF-8';
        if (!report.open('r')) { throw new Error('No response from the Composa exchange helper.'); }
        var result;
        try { result = report.read(); }
        finally { report.close(); report.remove(); }
        if (result.indexOf('ERROR:') === 0) { throw new Error(result.substring(6)); }
    } catch (error) {
        alert('Send layers to Composa failed: ' + error.message);
    } finally {
        if (launcher.exists) { launcher.remove(); }
        if (!submitted) {
            if (image.exists) { image.remove(); }
            folder.remove();
        }
    }

    function psLiteral(value) { return "'" + value.replace(/'/g, "''") + "'"; }

    // PowerShell expects base64 over UTF-16LE. ExtendScript has no built-in base64 encoder.
    function encodeCommand(value) {
        var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
        var bytes = [];
        for (var i = 0; i < value.length; i++) {
            var code = value.charCodeAt(i);
            bytes.push(code & 255, code >>> 8);
        }
        var result = '';
        for (var j = 0; j < bytes.length; j += 3) {
            var block = (bytes[j] << 16) | (bytes[j + 1] << 8) | bytes[j + 2];
            result += alphabet.charAt(block >>> 18) + alphabet.charAt((block >>> 12) & 63) +
                (j + 1 < bytes.length ? alphabet.charAt((block >>> 6) & 63) : '=') +
                (j + 2 < bytes.length ? alphabet.charAt(block & 63) : '=');
        }
        return result;
    }
}());
