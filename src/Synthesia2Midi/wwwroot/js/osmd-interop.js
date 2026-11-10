// Interop JS pour OpenSheetMusicDisplay, + lecture audio via osmd-audio-player
// (https://github.com/jimutt/osmd-audio-player) — synthétise le son directement à partir
// de la partition rendue et anime le curseur pendant la lecture.
window.osmdInterop = {
    _instances: {},
    _players: {},
    _durations: {},

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
            const duration = bpm > 0 ? wholes * 4 * 60 / bpm : 0;
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
    },
};
