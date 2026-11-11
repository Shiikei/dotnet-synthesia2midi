"""Contrôle de non-régression sur les morceaux de référence (voir references.json).

Usage (dans le conteneur, via run.sh) :
  check_reference.py check NOM [ID]   compare une conversion (par défaut la plus récente de la vidéo)
                                       aux valeurs validées (references.json) et à l'instantané figé
  check_reference.py freeze NOM ID    fige une conversion validée comme instantané de référence
                                       (copie MIDI / MusicXML / réglages dans /data/reference/NOM/)
  check_reference.py list             références et dernière conversion de chaque vidéo
Code de sortie : 0 = OK, 1 = écarts, 2 = erreur d'utilisation.
"""
import json, os, shutil, sqlite3, subprocess, sys, xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
DB = '/data/synthesia2midi.db'
REF_DIR = '/data/reference'

try:
    import mido
except ImportError:  # non persisté dans l'image : réinstallé à la demande
    subprocess.run([sys.executable, '-m', 'pip', 'install', '-q', '--break-system-packages', 'mido'], capture_output=True)
    import mido


def load_refs():
    with open(os.path.join(HERE, 'references.json'), encoding='utf-8') as f:
        return {k: v for k, v in json.load(f).items() if not k.startswith('_')}


def conversion(db, ref, conv_id=None):
    sql = "select Id, CreatedAt, SettingsJson, MidiFilePath, MusicXmlFilePath from Conversions where Status='Completed' and "
    row = (db.execute(sql + "upper(Id)=upper(?)", (conv_id,)) if conv_id
           else db.execute(sql + "SourceUrl=? order by CreatedAt desc limit 1", (ref['url'],))).fetchone()
    if row is None:
        sys.exit("Aucune conversion terminée trouvée" + (f" pour l'id {conv_id}" if conv_id else f" pour {ref['url']}"))
    return dict(zip(['id', 'created', 'settings', 'midi', 'xml'], row))


def metrics(midi_path, xml_path):
    m = mido.MidiFile(midi_path)
    tpb = m.ticks_per_beat
    tempo = next((x.tempo for t in m.tracks for x in t if x.type == 'set_tempo'), 500000)
    tracks, first_tick = [], None
    for t in m.tracks:
        ab, notes = 0, 0
        for x in t:
            ab += x.time
            if x.type == 'note_on' and x.velocity > 0:
                notes += 1
                first_tick = ab if first_tick is None else min(first_tick, ab)
        if notes:
            tracks.append({'name': next((x.name for x in t if x.type == 'track_name'), None),
                           'program': next((x.program for x in t if x.type == 'program_change'), None),
                           'notes': notes})
    root = ET.parse(xml_path).getroot()
    measures = root.findall('.//measure')
    incomplete, voices = 0, set()
    for meas in measures:
        dur = {}
        for n in meas.findall('note'):
            voices.add((n.findtext('staff'), n.findtext('voice')))
            if n.find('chord') is None:
                dur[n.findtext('staff')] = dur.get(n.findtext('staff'), 0) + int(n.findtext('duration') or 0)
        div = 4
        if any(v != 4 * div for v in dur.values()) or len(dur) != 2:
            incomplete += 1
    fifths = root.findtext('.//key/fifths')
    return {
        'bpm': round(60e6 / tempo, 2),
        'tracks': tracks,
        'notes': sum(t['notes'] for t in tracks),
        'first_note_beats': round((first_tick or 0) / tpb, 3),
        'measures': len(measures),
        'incomplete_measures': incomplete,
        'clefs': len(root.findall('.//clef')),
        'voices_per_staff': {s: len([v for (st, v) in voices if st == s]) for s in ('1', '2')},
        'fifths': int(fifths) if fifths is not None else None,
    }


def check(name, ref, conv):
    got = metrics(conv['midi'], conv['xml'])
    e = ref['expect']
    issues = []
    if abs(got['bpm'] - e['bpm']) > e['bpmTolerance']:
        issues.append(f"tempo {got['bpm']} au lieu de {e['bpm']} (±{e['bpmTolerance']})")
    if abs(got['measures'] - e['measures']) > e['measuresTolerance']:
        issues.append(f"{got['measures']} mesures au lieu de {e['measures']} (±{e['measuresTolerance']})")
    if e.get('fifths') is not None and got['fifths'] != e['fifths']:
        issues.append(f"armure {got['fifths']} au lieu de {e['fifths']}")
    if got['clefs'] != 2:
        issues.append(f"{got['clefs']} clés dans la partition (attendu : 2, sans changement)")
    if any(v != 1 for v in got['voices_per_staff'].values()):
        issues.append(f"voix par portée {got['voices_per_staff']} (attendu : 1 et 1)")
    if got['incomplete_measures']:
        issues.append(f"{got['incomplete_measures']} mesure(s) incomplète(s)")
    if got['first_note_beats'] > 0.25:
        issues.append(f"première note à {got['first_note_beats']} temps du début (attendu : sur le 1er temps)")
    if any(t['program'] != 0 for t in got['tracks']):
        issues.append(f"instrument absent ou différent du piano : {[t['program'] for t in got['tracks']]}")
    if e.get('handsSeparated') and len(got['tracks']) != 2:
        issues.append(f"{len(got['tracks'])} piste(s) de notes (attendu : 2 mains)")

    snap_path = os.path.join(REF_DIR, name, 'metrics.json')
    if os.path.exists(snap_path):
        with open(snap_path, encoding='utf-8') as f:
            snap = json.load(f)
        if abs(got['notes'] - snap['notes']) > max(3, 0.03 * snap['notes']):
            issues.append(f"{got['notes']} notes au lieu de {snap['notes']} (±3 %)")
        for g, s in zip(got['tracks'], snap['tracks']):
            if abs(g['notes'] - s['notes']) > max(3, 0.10 * s['notes']):
                issues.append(f"piste {s['name']} : {g['notes']} notes au lieu de {s['notes']} (±10 %)")
    else:
        issues.append("pas d'instantané figé (lancer freeze une fois la conversion validée)")

    print(f"[{name}] conversion {conv['id']} du {conv['created'][:16]}")
    print('  ' + json.dumps({k: got[k] for k in ('bpm', 'measures', 'fifths', 'notes', 'first_note_beats')}, ensure_ascii=False))
    print('  pistes : ' + ', '.join(f"{t['name']} ({t['notes']} notes, programme {t['program']})" for t in got['tracks']))
    if json.loads(conv['settings']) != ref['settings']:
        print(f"  attention : réglages différents de la référence {json.dumps(ref['settings'])}")
    print('  OK' if not issues else '  ÉCARTS :\n  - ' + '\n  - '.join(issues))
    return not issues


def freeze(name, ref, conv):
    target = os.path.join(REF_DIR, name)
    os.makedirs(target, exist_ok=True)
    shutil.copy2(conv['midi'], os.path.join(target, 'output.mid'))
    shutil.copy2(conv['xml'], os.path.join(target, 'score.musicxml'))
    video = next((os.path.join(os.path.dirname(conv['midi']), f) for f in os.listdir(os.path.dirname(conv['midi']))
                  if f.endswith(('.mp4', '.webm', '.mkv'))), None)
    if video:
        shutil.copy2(video, os.path.join(target, os.path.basename(video)))
    snap = metrics(conv['midi'], conv['xml'])
    snap.update({'conversion_id': conv['id'], 'created': conv['created'], 'settings': json.loads(conv['settings'])})
    with open(os.path.join(target, 'metrics.json'), 'w', encoding='utf-8') as f:
        json.dump(snap, f, ensure_ascii=False, indent=2)
    print(f"[{name}] instantané figé dans {target} : {json.dumps({k: snap[k] for k in ('bpm', 'measures', 'notes')})}")


def main(argv):
    refs = load_refs()
    if not argv or argv[0] not in ('check', 'freeze', 'list'):
        print(__doc__); return 2
    db = sqlite3.connect(DB)
    if argv[0] == 'list':
        for name, ref in refs.items():
            c = conversion(db, ref)
            print(f"{name}: dernière conversion {c['id']} du {c['created'][:16]} — figée : {os.path.exists(os.path.join(REF_DIR, name, 'metrics.json'))}")
        return 0
    names = [argv[1]] if len(argv) > 1 and argv[1] != 'all' else list(refs)
    if argv[0] == 'freeze':
        if len(argv) < 3:
            print('freeze NOM ID : l\'id de la conversion validée est obligatoire'); return 2
        freeze(argv[1], refs[argv[1]], conversion(db, refs[argv[1]], argv[2])); return 0
    ok = all([check(n, refs[n], conversion(db, refs[n], argv[2] if len(argv) > 2 else None)) for n in names])
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
