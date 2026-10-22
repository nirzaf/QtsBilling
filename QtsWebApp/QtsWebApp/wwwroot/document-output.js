// Local document output for QTS Billing (specification task 24, recipe step 3).
//
// The document handed here has already been fetched by the application's own authenticated client. This script
// therefore receives content and never a location: there is no navigate, no fetch, no query string and no token
// to leak into history, an address bar, a Referer header or a log. Nothing is written to disk either - a PDF is
// produced only if the operator chooses "Save as PDF" in the print dialogue, in the folder they name.
//
// The frame is same-origin (about:blank) rather than a srcdoc frame, because a frame with an opaque origin
// cannot be asked to print. The snapshot is written into its own document, so the application's navigation,
// header and any half-typed form never reach the paper.
window.qtsDocuments = window.qtsDocuments || {
    print: function (title, html) {
        if (typeof html !== "string" || html.indexOf("<html") < 0) {
            // Not a rendered document: refuse rather than print whatever a proxy or error page returned.
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

        // A dialogue the operator dismisses never fires afterprint in every browser, so the frame is also
        // cleaned up on a timer. Its content is the document the operator was shown on screen a moment ago.
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
