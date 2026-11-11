// Interop JS pour OpenSheetMusicDisplay, + lecture audio via osmd-audio-player
// (https://github.com/jimutt/osmd-audio-player) — synthétise le son directement à partir
// de la partition rendue et anime le curseur pendant la lecture.
window.osmdInterop = {
    _instances: {},
    _players: {},
    _durations: {},
    _baseBpm: {},   // tempo écrit dans la partition
    _wholes: {},    // durée totale en rondes
    _speed: {},     // facteur de vitesse de lecture (1 = tempo écrit)

    // Rend la partition et renvoie { bpm, timeSignature, duration } pour l'en-tête de la
    // feuille et la barre de transport (null si le conteneur n'existe pas).
    render: async function (containerId, musicXmlUrl) {
        const container = document.getElementById(containerId);
        if (!container) {
            return null;
        }

        if (!this._instances[containerId]) {
            this._instances[containerId] = new opensheetmusicdisplay.OpenSheetMusicDisplay(container, {
                autoResize: true,
                // Notre MusicXML (MusicXmlScoreWriter) ne contient pas de ligatures : OSMD les calcule.
                autoBeam: true,
                backend: "svg",
                // Le titre est déjà posé par la feuille (ScoreSheet) au-dessus de la gravure.
                drawTitle: false,
                drawSubtitle: false,
                drawComposer: false,
                drawLyricist: false,
                drawCredits: false,
                drawPartNames: false,
                // Curseur de lecture en paper-cursor (cuivre fixe, lisible sur le blanc).
                cursorsOptions: [{ type: 0, color: "#a8470a", alpha: 0.35, follow: true }],
            });
        }

        const osmd = this._instances[containerId];
        await osmd.load(musicXmlUrl);
        osmd.render();

        if (!this._players[containerId]) {
            this._players[containerId] = new OsmdAudioPlayer();
        }
        const player = this._players[containerId];
        await player.loadScore(osmd);

        return this._describe(containerId, osmd, player);
    },

    _describe: function (containerId, osmd, player) {
        try {
            const sheet = osmd.Sheet;
            const measures = (sheet && sheet.SourceMeasures) || [];
            const first = measures[0];
            const bpm = (player && player.playbackSettings && player.playbackSettings.bpm)
                || (first && first.TempoInBPM)
                || (sheet && sheet.DefaultStartTempoInBpm)
                || 0;
            const sig = first && first.ActiveTimeSignature
                ? first.ActiveTimeSignature.Numerator + "/" + first.ActiveTimeSignature.Denominator
                : null;

            // Durée en rondes de chaque mesure → secondes au tempo de lecture.
            let wholes = 0;
            for (const m of measures) {
                if (m && m.Duration && m.Duration.RealValue) {
                    wholes += m.Duration.RealValue;
                }
            }
            this._baseBpm[containerId] = bpm;
            this._wholes[containerId] = wholes;
            this._speed[containerId] = this._speed[containerId] || 1;
            if (this._speed[containerId] !== 1) {
                this._applyBpm(player, bpm * this._speed[containerId]);
            }
            const duration = bpm > 0 ? wholes * 4 * 60 / (bpm * this._speed[containerId]) : 0;
            this._durations[containerId] = duration;

            return { bpm: bpm > 0 ? Math.round(bpm) : null, timeSignature: sig, duration: duration };
        } catch (e) {
            return { bpm: null, timeSignature: null, duration: 0 };
        }
    },

    status: function (containerId) {
        const player = this._players[containerId];
        if (!player) {
            return null;
        }
        const total = player.iterationSteps || 0;
        const current = player.currentIterationStep || 0;
        const duration = this._durations[containerId] || 0;
        return {
            state: String(player.state || "STOPPED"),
            position: total > 0 ? duration * Math.min(1, current / total) : 0,
            duration: duration,
        };
    },

    // Vitesse de lecture (facteur, ex. 1.25 = 25 % plus vite). Pendant la lecture, on met en
    // pause, on change le tempo puis on reprend au même endroit : le lecteur ne recalcule pas
    // toujours les notes déjà programmées. Renvoie { bpm, duration } pour la barre de transport.
    setSpeed: function (containerId, factor) {
        const player = this._players[containerId];
        const base = this._baseBpm[containerId] || 0;
        if (!player || !(base > 0) || !(factor > 0)) {
            return null;
        }
        this._speed[containerId] = factor;
        const bpm = base * factor;
        const wasPlaying = player.state === "PLAYING";
        if (wasPlaying) {
            player.pause();
        }
        this._applyBpm(player, bpm);
        if (wasPlaying) {
            player.play();
        }
        const duration = (this._wholes[containerId] || 0) * 4 * 60 / bpm;
        this._durations[containerId] = duration;
        return { bpm: Math.round(bpm), duration: duration };
    },

    _applyBpm: function (player, bpm) {
        if (typeof player.setBpm === "function") {
            player.setBpm(bpm);
        } else if (player.playbackSettings) {
            player.playbackSettings.bpm = bpm;
        }
    },

    play: function (containerId) {
        const player = this._players[containerId];
        if (player && (player.state === "STOPPED" || player.state === "PAUSED")) {
            player.play();
        }
    },

    pause: function (containerId) {
        const player = this._players[containerId];
        if (player && player.state === "PLAYING") {
            player.pause();
        }
    },

    stop: function (containerId) {
        const player = this._players[containerId];
        if (player && (player.state === "PLAYING" || player.state === "PAUSED")) {
            player.stop();
        }
    },

    // Appelé quand on quitte la page : couper le son et oublier l'instance.
    dispose: function (containerId) {
        this.stop(containerId);
        delete this._players[containerId];
        delete this._instances[containerId];
        delete this._durations[containerId];
        delete this._baseBpm[containerId];
        delete this._wholes[containerId];
        delete this._speed[containerId];
    },
};
