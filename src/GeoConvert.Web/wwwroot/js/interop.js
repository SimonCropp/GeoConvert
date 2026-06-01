window.statePreference = {
    get: function (key) {
        return localStorage.getItem(key);
    },
    set: function (key, value) {
        localStorage.setItem(key, value);
    },
    remove: function (key) {
        localStorage.removeItem(key);
    }
};

window.fileDownload = {
    downloadBlob: function (filename, contentType, base64Content) {
        const byteCharacters = atob(base64Content);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
    }
};

window.appInfo = {
    userAgent: function () {
        return navigator.userAgent;
    },
    // Resolve once the browser has actually painted the pending DOM update, before the caller
    // starts long-running synchronous work that blocks the (single) WASM UI thread. A bare
    // `await Task.Delay(1)` only flushes Blazor's DOM diff; the 1 ms timer callback then runs the
    // blocking render before the browser composites a frame, so a just-shown spinner never paints.
    // A double requestAnimationFrame waits for a genuinely painted frame; the setTimeout is a
    // fallback for hidden/background tabs where rAF is paused, so this never hangs.
    paintThen: function () {
        return new Promise(function (resolve) {
            var done = false;
            var finish = function () { if (!done) { done = true; resolve(); } };
            requestAnimationFrame(function () { requestAnimationFrame(finish); });
            setTimeout(finish, 100);
        });
    }
};

window.themeManager = {
    applyTheme: function (themeName) {
        document.documentElement.setAttribute('data-theme', themeName.toLowerCase());
    },
    initializeTheme: function () {
        const savedTheme = localStorage.getItem('selectedTheme');
        if (savedTheme) {
            document.documentElement.setAttribute('data-theme', savedTheme.toLowerCase());
        }
    }
};
