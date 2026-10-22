// Local document output for the Windows device host (specification task 24, recipe step 3).
//
// This is the same rule as in the web host, restated because the assets belong to this host: the snapshot has
// already been fetched over the device's authenticated bearer client, so the print path receives content and
// never a location. The device therefore never navigates a WebView to a bearer-protected route, and a token
// never enters a URL, a navigation history entry or a file name. Nothing is written to disk here either: the
// print dialogue is the operating system's own, and "Microsoft Print to PDF" saves where the operator chooses.
//
// A same-origin (about:blank) frame is used rather than srcdoc, because a frame with an opaque origin cannot be
// asked to print, and the application's own chrome (navigation drawer, half-typed forms) must not reach the paper.
window.qtsDocuments = window.qtsDocuments || {
    print: function (title, html) {
        if (typeof html !== "string" || html.indexOf("<html") < 0) {
            return false;
        }

        var frame = document.createElement("iframe");
        frame.setAttribute("aria-hidden", "true");
        frame.setAttribute("title", title || "Document");
        frame.style.position = "fixed";
        frame.style.right = "0";
        frame.style.bottom = "0";
        frame.style.width = "0";
        frame.style.height = "0";
        frame.style.border = "0";
        frame.src = "about:blank";
        document.body.appendChild(frame);

        var doc = frame.contentDocument;
        if (!doc) {
            frame.remove();
            return false;
        }

        doc.open();
        doc.write(html);
        doc.close();
        doc.title = title || "Document";

        var remove = function () {
            window.setTimeout(function () {
                if (frame.isConnected) {
                    frame.remove();
                }
            }, 400);
        };

        frame.contentWindow.addEventListener("afterprint", remove);
        window.setTimeout(remove, 120000);

        try {
            frame.contentWindow.focus();
            frame.contentWindow.print();
        } catch (error) {
            remove();
            return false;
        }

        return true;
    }
};
