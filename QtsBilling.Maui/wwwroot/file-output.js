// Local file output for QTS Billing (specification task 33, recipe step 3).
//
// The bytes handed here were already fetched by the application's own authenticated client, so this bridge receives
// content and a bare file name - never a URL, never a token, never a path. There is no fetch, no navigate and no
// remote share: a report leaves the machine only when the operator's browser puts it in the folder they chose.
//
// The download is an object URL over the Blob the caller passed, revoked straight after the click so the statement
// does not stay addressable in the page for the rest of the session.
window.qtsFiles = window.qtsFiles || {
    save: function (fileName, base64Content) {
        if (typeof fileName !== "string" || fileName.length === 0) {
            return false;
        }

        // A bare file name only. A separator, a parent segment or an absolute prefix is refused here as well as in
        // the contract, because this is the place where a name becomes a place on somebody's disk.
        if (fileName.indexOf("/") >= 0
            || fileName.indexOf("\\") >= 0
            || fileName.indexOf(":") >= 0
            || fileName.indexOf("..") >= 0) {
            return false;
        }

        // The type comes from the extension and from this closed list only: the report export and the evidence
        // formats the server holds. An active format has no place here (specification tasks 33 and 39).
        var types = {
            csv: "text/csv;charset=utf-8",
            pdf: "application/pdf",
            jpg: "image/jpeg",
            jpeg: "image/jpeg",
            png: "image/png",
        };

        var lastDot = fileName.lastIndexOf(".");
        var extension = lastDot > 0 ? fileName.slice(lastDot + 1).toLowerCase() : "";

        if (!Object.prototype.hasOwnProperty.call(types, extension)) {
            return false;
        }

        if (typeof base64Content !== "string" || base64Content.length === 0) {
            return false;
        }

        var bytes;
        try {
            var binary = atob(base64Content);
            bytes = new Uint8Array(binary.length);
            for (var index = 0; index < binary.length; index++) {
                bytes[index] = binary.charCodeAt(index);
            }
        } catch (error) {
            return false;
        }

        if (bytes.length === 0) {
            return false;
        }

        var blob = new Blob([bytes], { type: types[extension] });
        var url = URL.createObjectURL(blob);
        var anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName;
        anchor.rel = "noopener";
        anchor.style.display = "none";
        document.body.appendChild(anchor);

        try {
            anchor.click();
        } catch (error) {
            anchor.remove();
            URL.revokeObjectURL(url);
            return false;
        }

        anchor.remove();
        // Released on the next turn so the click has taken the blob with it.
        window.setTimeout(function () {
            URL.revokeObjectURL(url);
        }, 4000);

        return true;
    }
};
