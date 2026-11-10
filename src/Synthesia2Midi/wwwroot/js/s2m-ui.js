// Petits utilitaires d'interface du design system syn2mid (thème, ligne du clavier).
window.s2mUi = {
    themeKey: "syn2mid-theme",

    getTheme: function () {
        try { return window.localStorage.getItem(this.themeKey) || "system"; } catch (e) { return "system"; }
    },

    setTheme: function (pref) {
        try { window.localStorage.setItem(this.themeKey, pref); } catch (e) { }
        this.applyTheme(pref);
    },

    applyTheme: function (pref) {
        var dark = pref === "dark";
        if (pref !== "light" && pref !== "dark") {
            try { dark = window.matchMedia("(prefers-color-scheme: dark)").matches; } catch (e) { dark = false; }
        }
        document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
    },

    // Ligne du clavier sur l'aperçu vidéo : un glisser vertical sur l'image règle
    // KeyboardHeight (fraction de la hauteur de l'image, comme côté analyse).
    _bound: new WeakSet(),

    bindKeyboardLine: function (frame, dotnet) {
        if (!frame || this._bound.has(frame)) {
            return;
        }
        this._bound.add(frame);

        var dragging = false;
        var last = null;

        function send(e) {
            var r = frame.getBoundingClientRect();
            if (!r.height) {
                return;
            }
            var v = (e.clientY - r.top) / r.height;
            v = Math.round(Math.min(0.99, Math.max(0.01, v)) * 100) / 100;
            if (v !== last) {
                last = v;
                dotnet.invokeMethodAsync("SetKeyboardLine", v).catch(function () { });
            }
        }

        frame.addEventListener("pointerdown", function (e) {
            if (frame.classList.contains("is-playing")) {
                return;
            }
            dragging = true;
            try { frame.setPointerCapture(e.pointerId); } catch (err) { }
            e.preventDefault();
            send(e);
        });
        frame.addEventListener("pointermove", function (e) {
            if (dragging) {
                send(e);
            }
        });
        var stop = function () { dragging = false; };
        frame.addEventListener("pointerup", stop);
        frame.addEventListener("pointercancel", stop);
    },
};

// Suivre le réglage de l'appareil tant que le choix est « Système ».
(function () {
    try {
        var mq = window.matchMedia("(prefers-color-scheme: dark)");
        var onChange = function () {
            if (window.s2mUi.getTheme() === "system") {
                window.s2mUi.applyTheme("system");
            }
        };
        if (mq.addEventListener) { mq.addEventListener("change", onChange); } else if (mq.addListener) { mq.addListener(onChange); }
    } catch (e) { }
})();

// Test du seuil sur un extrait : rouleau de notes dessiné à partir des écarts de
// luminosité par image et par touche (calculés côté serveur une seule fois). Changer
// le seuil ne fait que redessiner ici, sans aller-retour d'analyse.
window.s2mUi.probe = {
    data: null,
    canvas: null,
    threshold: 30,
    split: false,
    _observed: null,
    _themeObserver: null,

    load: function (canvas, frames, keys, isWhite, diffs, hands, threshold, split) {
        this.data = {
            frames: frames,
            keys: keys,
            isWhite: isWhite || [],
            diffs: this._bytes(diffs),
            hands: this._bytes(hands)
        };
        this.threshold = threshold;
        this.split = !!split;
        this.attach(canvas);
    },

    attach: function (canvas) {
        if (!canvas) {
            return;
        }
        this.canvas = canvas;
        var self = this;
        if (window.ResizeObserver && this._observed !== canvas) {
            this._observed = canvas;
            try { new ResizeObserver(function () { self.draw(); }).observe(canvas); } catch (e) { }
        }
        if (!this._themeObserver && window.MutationObserver) {
            try {
                this._themeObserver = new MutationObserver(function () { self.draw(); });
                this._themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
            } catch (e) { }
        }
        this.draw();
    },

    update: function (threshold, split) {
        this.threshold = threshold;
        this.split = !!split;
        this.draw();
    },

    clear: function () {
        this.data = null;
        this.draw();
    },

    _bytes: function (v) {
        if (!v) { return new Uint8Array(0); }
        if (v instanceof Uint8Array) { return v; }
        if (typeof v === "string") {
            var bin = atob(v);
            var out = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
            return out;
        }
        return new Uint8Array(v);
    },

    draw: function () {
        var c = this.canvas;
        if (!c || !c.isConnected) {
            return;
        }
        var cssW = c.clientWidth || 300;
        var cssH = c.clientHeight || 150;
        var dpr = window.devicePixelRatio || 1;
        if (c.width !== Math.round(cssW * dpr) || c.height !== Math.round(cssH * dpr)) {
            c.width = Math.round(cssW * dpr);
            c.height = Math.round(cssH * dpr);
        }
        var ctx = c.getContext("2d");
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        var css = getComputedStyle(c);
        var v = function (name, fallback) { return (css.getPropertyValue(name) || "").trim() || fallback; };
        var ground = v("--surface-sunken", "#ebe8e1");
        var blackRow = v("--line", "#dedad1");
        var right = v("--hand-right", v("--accent", "#a8470a"));
        var left = v("--hand-left", "#2459d6");

        ctx.fillStyle = ground;
        ctx.fillRect(0, 0, cssW, cssH);

        var d = this.data;
        if (!d || !d.frames || !d.keys) {
            return;
        }

        var rowH = cssH / d.keys;

        // Rangées des touches noires, un ton plus sombre, comme un rouleau de piano.
        ctx.fillStyle = blackRow;
        for (var k = 0; k < d.keys; k++) {
            if (d.isWhite[k] === false) {
                ctx.fillRect(0, cssH - (k + 1) * rowH, cssW, rowH);
            }
        }

        var frames = d.frames;
        var keys = d.keys;
        var t = this.threshold;
        var xOf = function (f) { return f / frames * cssW; };
        var barH = Math.max(1, rowH - (rowH > 3 ? 1 : 0));

        for (var key = 0; key < keys; key++) {
            var pressed = false;
            var onset = 0;
            var y = cssH - (key + 1) * rowH + (rowH - barH) / 2;
            for (var f = 0; f <= frames; f++) {
                var now = f < frames && d.diffs[f * keys + key] > t;
                if (now === pressed) {
                    continue;
                }
                if (now) {
                    onset = f;
                } else {
                    var leftHand = this.split && d.hands[onset * keys + key] === 1;
                    ctx.fillStyle = leftHand ? left : right;
                    var x0 = xOf(onset);
                    ctx.fillRect(x0, y, Math.max(1.5, xOf(f) - x0), barH);
                }
                pressed = now;
            }
        }
    }
};

// Online Sequencer n'importe pas un MIDI par lien : on télécharge le fichier puis on
// ouvre l'éditeur, où l'import se fait par son menu « Import MIDI/Sequence File ».
window.s2mUi.openInSequencer = function (href, name) {
    try {
        var a = document.createElement("a");
        a.href = href;
        a.download = name || "conversion.mid";
        a.style.display = "none";
        document.body.appendChild(a);
        a.click();
        a.remove();
    } catch (e) { }
    window.open("https://onlinesequencer.net/", "_blank", "noopener");
};
