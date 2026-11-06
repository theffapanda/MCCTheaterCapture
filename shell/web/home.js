(() => {
  'use strict';
  const $ = id => document.getElementById(id), bridge = window.chrome?.webview;
  let serial = 0, films = [], draft, batch = {}, singleJob, saving, saveTimer, busy = false;
  let artwork = {}, selectedPath = '', sequenceDrag = null;
  let highlights = {}, highlightSelection = new Set(), highlightResultKey = '';
  let highlightDiagnosticsKey = '', highlightDiagnosticsRendered = false;
  let formatClipId = '';
  const highlightMode = () => $('capture-length').value === 'highlights';
  const pending = new Map();
  function native(type, data = {}, files) {
    if (!bridge) return Promise.reject(new Error('Open Export desk in MCC Theater Capture to access your films.'));
    return new Promise((resolve, reject) => {
      const id = ++serial; pending.set(id, {resolve, reject});
      try {
        if (files) bridge.postMessageWithAdditionalObjects({id, type, ...data}, files);
        else bridge.postMessage({id, type, ...data});
      } catch (e) {pending.delete(id); reject(e);}
    });
  }
  bridge?.addEventListener('message', ({data}) => {
    if (data.type === 'highlights-status') {receiveHighlights(data.data);renderSummary();renderLibrary();renderSequence();return;}
    if (data.type === 'batch-status') {batch = data.data; renderJob(); renderSummary(); return;}
    if (data.type === 'background-recording') {showBackground(data.job); renderSummary(); return;}
    const item = pending.get(data.id); if (!item) return; pending.delete(data.id);
    data.ok ? item.resolve(data.data) : item.reject(new Error(data.error));
  });
  const error = e => {$('status').textContent = e.message || String(e);$('library-status').textContent = $('status').textContent;};
  const el = (tag, text, cls) => {const node = document.createElement(tag); if (text != null) node.textContent = text; if (cls) node.className = cls; return node;};
  const mapTitle = film => films.find(f => f.path?.toLowerCase() === (film?.path || film?.film || '').toLowerCase())?.title || film?.title || 'halo3';
  const displayMap = (name, film) => window.MccMapNames?.display(name, mapTitle(film)) || window.Halo3MapNames?.display(name) || name;
  const plural = (n, noun) => `${n} ${noun}${n === 1 ? '' : 's'}`;
  const locked = () => !draft || busy || !!batch.running || !!highlights.running || !!highlights.scanning;
  // A checkpoint describes an immutable export attempt, not an editor lock.
  // The first edit forks its sources/settings into a fresh draft; Retry keeps
  // targeting the original job and its verified completed clips.
  function editableDraft() {
    if (!draft?.signature) return;
    if (!batch.job) batch.job = structuredClone(draft);
    const next = fresh(draft);
    next.items = draft.items.map(i => ({...i,state:'queued',file:'',error:'',frames:0,sha256:'',durationSeconds:0}));
    draft = next;
  }
  function art(map, film) {
    if (film?.artworkKey && artwork[film.artworkKey]) return artwork[film.artworkKey];
    const title = mapTitle(film);
    const converted = window.MccMapNames?.artworkKey(map, title);
    if (converted && artwork[converted]) return artwork[converted];
    if (title !== 'halo3') return artwork[title + ':' + String(map || '').toUpperCase()];
    const key = displayMap(map, film).replace(/[^a-z0-9]/gi, '').toUpperCase();
    return artwork[({EDGE:'S3D_EDGE',WATERFALL:'S3D_WATERFALL',ICEBOX:'S3D_ICEBOX'})[key] || key];
  }
  function image(map, container, film) {
    const url = art(map, film); if (!url) return;
    const img = el('img', null, 'map-image'); img.src = url;img.alt = '';img.loading = 'lazy';img.draggable = false;
    img.onerror = () => img.remove(); container.prepend(img);
  }
  function selectedFilm() {
    const matches = film => (film.path || film.film).toLowerCase() === selectedPath.toLowerCase();
    return films.find(matches) || draft?.items.find(matches) || draft?.items[0] || films[0];
  }
  function selectFilm(film) {
    selectedPath = film.path || film.film; renderHero();
    document.querySelectorAll('.sequence-row').forEach(row => {const selected=row.dataset.path.toLowerCase() === selectedPath.toLowerCase();row.classList.toggle('selected',selected);row.querySelector('.sequence-preview').setAttribute('aria-pressed',String(selected));});
  }
  function renderHero() {
    const film = selectedFilm(), url = film && art(film.map, film);
    if (film) selectedPath = film.path || film.film;
    $('hero-title').textContent = film ? displayMap(film.map, film) : 'Your next reel.';
    $('hero-meta').textContent = film ? `${film.titleName ? film.titleName + ' · ' : ''}${film.gameVariantName || film.gameType || 'Original Theater film'} · ${film.duration || duration(film.totalTicks / 60)} · Original POV` : 'Add films to build your sequence.';
    $('hero-filename').textContent = film?.name || '';
    $('hero-reason').textContent = film?.reason || '';
    $('scenery').style.backgroundImage = url ? `url("${url}")` : 'none';
    $('hero-art-label').textContent = url ? `MCC MAP ARTWORK · ${displayMap(film.map, film).toUpperCase()}` : 'ORIGINAL FILMS. NATIVE HALO RENDERING.';
    const known = film && films.find(f => f.path.toLowerCase() === selectedPath.toLowerCase());
    const queued = !!film && !!draft?.items.some(i => i.film.toLowerCase() === selectedPath.toLowerCase());
    $('hero-open').disabled = !known?.canDecode || !!batch.running || busy || !!(singleJob && !singleJob.finished);
    $('hero-open').innerHTML = '<span aria-hidden="true">▶</span>';
    $('hero-open').append(document.createTextNode($('background-recording').checked ? ' Export single film' : ' Open playback'));
    $('hero-add').disabled = locked() || !known?.canDecode || queued;
    $('hero-add').textContent = queued ? '✓ In sequence' : '+ Add to sequence';
    $('asset-status').textContent = Object.keys(artwork).length ? 'MCC ARTWORK · LOCAL GAME FILES' : 'LOCAL WORKSPACE · MAP ART UNAVAILABLE';
  }
  function openLibrary() {if (!$('library-dialog').open) $('library-dialog').showModal();$('search').focus();}
  function closeLibrary() {$('library-dialog').close();}
  function moveItem(from, to) {
    if (locked() || from === to || !Number.isInteger(from) || !Number.isInteger(to) || from < 0 || to < 0 || from >= draft.items.length || to >= draft.items.length) return;
    editableDraft();
    const [item] = draft.items.splice(from, 1);draft.items.splice(to, 0, item);
    renderLibrary();renderSequence();renderSummary();persist();
  }
  function duration(seconds) {seconds = Math.max(0, Math.round(seconds));return seconds >= 3600 ? `${Math.floor(seconds / 3600)}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}` : `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;}
  function progressText(job, value) {
    const percent = Number.isFinite(value) ? `${Math.floor(Math.max(0, Math.min(1, value)) * 100)}%` : 'Progress pending';
    if (job.complete || job.state === 'completed') return '100% · Complete';
    if (job.finished) return `${percent} · ${job.cancelled || job.state === 'cancelled' ? 'Cancelled' : 'Stopped'}`;
    if (['joining','finalizing','stopping'].includes(job.state)) return `${percent} · ${job.state === 'joining' ? 'Joining clips' : 'Finalizing export'} · Estimating remaining time…`;
    const eta = value => Number.isFinite(value) ? duration(value) : 'Estimating…';
    if (Array.isArray(job.items)) return `${percent} · Current film: ${eta(job.currentFilmSecondsRemaining)} · Whole job: ${eta(job.estimatedSecondsRemaining)} remaining + loading / finalization${job.mode !== 'separate' ? ' / joining' : ''}`;
    return `${percent} · Current film: ${eta(job.estimatedSecondsRemaining)} remaining + finalization`;
  }
  function fresh(previous) {
    return {schema:'mcc-batch/v1', id:crypto.randomUUID().replaceAll('-', ''), name:previous?.name || 'Theater reel', mode:previous?.mode || 'separate', outputFormat:previous?.outputFormat || 'mp4', outputResolution:previous?.outputResolution || 1440, outputQuality:previous?.outputQuality || 'high', frameRate:previous?.frameRate || 60, fov:previous?.fov || batch.fov || 91, captureKind:previous?.captureKind || 'full', highlightOptions:previous?.highlightOptions || {minimumKills:4,exact:false,selection:'all',beforeSeconds:3,afterSeconds:3}, highlightFormat:structuredClone(previous?.highlightFormat || {layout:'original',blur:22,dim:40,gameplayPosition:48}),highlightCaptions:structuredClone(previous?.highlightCaptions || {}),sampleSeconds:previous?.sampleSeconds || null, continueOnError:previous?.continueOnError ?? true, items:[], signature:'', state:'draft'};
  }
  function readControls() {
    if (!draft || locked()) return;
    editableDraft();
    draft.name = $('reel-name').value;
    draft.outputFormat = $('output-format').value;
    draft.outputResolution = Number($('output-resolution').value);
    draft.outputQuality = $('output-quality').value;
    draft.frameRate = Number($('output-fps').value);
    draft.mode = document.querySelector('input[name=mode]:checked').value;
    draft.fov = Number($('batch-fov').value);
    draft.sampleSeconds = $('capture-length').value === 'sample' ? Number($('sample-seconds').value) : null;
    draft.continueOnError = $('continue-errors').checked;
    draft.captureKind = $('capture-length').value;draft.highlightOptions = highlightOptions();
    draft.highlightFormat = highlightFormatOptions();
  }
  function applyDraft(plan) {
    draft = plan || fresh(); draft.items ||= [];
    $('output-format').value = draft.outputFormat || 'mp4';
    $('output-resolution').value = draft.outputResolution || 1440;
    $('output-quality').value = draft.outputQuality || 'high';
    $('output-fps').value = draft.frameRate || 60;
    $('reel-name').value = draft.name; $('batch-fov').value = draft.fov;
    $('capture-length').value = draft.captureKind === 'highlights' ? 'highlights' : draft.sampleSeconds != null ? 'sample' : 'full';
    applyHighlightOptions(draft.highlightOptions || highlights.scan?.options);
    applyHighlightFormat(draft.highlightFormat);formatClipId = '';
    $('sample-seconds').value = draft.sampleSeconds ?? 10; $('continue-errors').checked = draft.continueOnError;
    for (const radio of document.querySelectorAll('input[name=mode]')) radio.checked = radio.value === draft.mode;
    renderLibrary(); renderSequence(); renderSummary(); renderJob();
  }
  function persist() {
    clearTimeout(saveTimer);
    // Serialize requests so an older autosave cannot replace a newer draft.
    const snapshot = JSON.parse(JSON.stringify(draft));
    saving = (saving || Promise.resolve()).catch(() => {}).then(() => native('batch-save', {plan:snapshot}));
    saving.catch(error); return saving;
  }
  function changed() {readControls(); renderSummary(); clearTimeout(saveTimer); saveTimer = setTimeout(persist, 250);}
  function itemFrom(film) {return {film:film.path, name:film.name, map:film.map, title:film.title || 'halo3', totalTicks:film.totalTicks, sourceBytes:film.bytes, sourceModified:film.modified, state:'queued', file:'', error:'', frames:0, sha256:''};}
  function addFilms(selected) {
    if (locked()) return;
    editableDraft();
    const existing = new Set(draft.items.map(i => i.film.toLowerCase()));
    let added = 0;
    for (const film of selected) if (film.canDecode && !existing.has(film.path.toLowerCase())) {draft.items.push(itemFrom(film)); existing.add(film.path.toLowerCase()); added++;}
    $('status').textContent = added ? `Added ${plural(added, 'film')} to your sequence.` : 'Those films are already in the sequence, or need a matching map.';
    $('library-status').textContent = $('status').textContent;
    if (added) selectedPath = selected[0].path;
    renderLibrary(); renderSequence(); renderSummary(); persist();
  }
  function visibleFilms() {const query = $('search').value.toLowerCase().trim();return films.filter(f => `${f.titleName || ''} ${displayMap(f.map, f)} ${f.name} ${f.gameVariantName || f.gameType || ''}`.toLowerCase().includes(query));}
  function renderLibrary() {
    const shown = visibleFilms(); $('films').replaceChildren(); $('library-count').textContent = `${shown.length} / ${plural(films.length, 'film')}`;
    const queued = new Set((draft?.items || []).map(i => i.film.toLowerCase()));
    for (const film of shown) {
      const selected = queued.has(film.path.toLowerCase()), row = el('div', null, `film${selected ? ' queued' : ''}`);
      const thumbnail = el('div', null, 'film-thumbnail');image(film.map, thumbnail, film);row.append(thumbnail);
      const title = el('button', displayMap(film.map, film), 'film-title');title.title = film.name;title.onclick = () => {selectFilm(film);closeLibrary();};
      const info = el('div'); info.append(title, el('div', film.gameVariantName || film.gameType || film.name, 'film-mode'));
      const meta = el('div', null, 'film-meta'); if(film.titleName) meta.append(el('span',film.titleName)); meta.append(el('span', film.duration || duration(film.totalTicks / 60)));
      const open = el('button', 'Open', 'text-button'); open.title = film.name; open.disabled = !!batch.running;
      open.disabled ||= !film.canDecode || busy || !!(singleJob && !singleJob.finished);
      open.onclick = () => {closeLibrary();native('open-film', {filmId:film.id}).catch(error);}; meta.append(open); info.append(meta);
      if (!film.canDecode) info.append(el('div', film.reason, 'film-reason'));
      row.append(info);
      const add = el('button', selected ? '✓' : '+', `film-add${selected ? ' added' : ''}`); add.disabled = locked() || selected || !film.canDecode;
      add.setAttribute('aria-label', `${selected ? 'Queued' : 'Add'} ${film.name}`); add.title = selected ? 'Already in your sequence' : `Add ${film.name}`; add.onclick = () => addFilms([film]); row.append(add); $('films').append(row);
    }
    if (!shown.length) $('films').append(el('p', films.length ? 'No films match your search.' : 'Choose Theater files or set your Theater folder.', 'fine'));
  }
  function renderSequence() {
    $('sequence').replaceChildren(); $('sequence-empty').hidden = draft.items.length > 0; $('reel-count').textContent = plural(draft.items.length, 'film');
    if (!selectedPath) selectedPath = draft.items[0]?.film || films[0]?.path || '';
    draft.items.forEach((item, index) => {
      const film = films.find(f => f.path.toLowerCase() === item.film.toLowerCase());
      const row = el('div', null, `sequence-row${item.film.toLowerCase() === selectedPath.toLowerCase() ? ' selected' : ''}`);row.setAttribute('role', 'listitem');row.dataset.path = item.film;row.dataset.index = index;row.draggable = !locked();image(item.map, row, film || item);
      row.append(el('span', String(index + 1).padStart(2, '0'), 'sequence-number'));
      const preview = el('button', null, 'sequence-preview');preview.title = `Preview ${item.name}`;preview.setAttribute('aria-label', `Select ${displayMap(item.map, item)}, ${item.name}`);preview.setAttribute('aria-pressed',String(row.classList.contains('selected')));preview.onclick = () => selectFilm(item);row.append(preview);
      const info = el('div', null, 'sequence-info');info.append(el('strong', displayMap(item.map, item)), el('small', film?.gameVariantName || film?.gameType || item.name), el('small', duration(Math.min(item.totalTicks / 60, draft.sampleSeconds ?? Infinity))));row.append(info);
      const controls = el('div', null, 'sequence-controls');
      for (const [text, label, offset] of [['↑','Move up',-1], ['↓','Move down',1], ['×','Remove',0]]) {
        const button = el('button', text);button.setAttribute('aria-label', `${label} ${item.name}`);button.title = label;
        button.disabled = locked() || (offset < 0 && index === 0) || (offset > 0 && index === draft.items.length - 1);
        button.onclick = () => {
          if (locked()) return;
          editableDraft();
          if (offset) {moveItem(index, index + offset);$('sequence').children[index + offset]?.querySelector('.sequence-controls button:not(:disabled)')?.focus();return;}
          draft.items.splice(index, 1);
          if (selectedPath.toLowerCase() === item.film.toLowerCase()) selectedPath = draft.items[Math.min(index, draft.items.length - 1)]?.film || films[0]?.path || '';
          renderLibrary();renderSequence();renderSummary();persist();
          // Keep keyboard focus near the moved item.
          $('sequence').children[Math.min(draft.items.length - 1, Math.max(0,index + offset))]?.querySelector('button:not(:disabled)')?.focus();
        };controls.append(button);
      }
      row.ondragstart = event => {if (locked()) {event.preventDefault();return;}sequenceDrag = index;event.dataTransfer.effectAllowed = 'move';event.dataTransfer.setData('text/plain', String(index));row.classList.add('dragging');};
      row.ondragover = event => {if (sequenceDrag == null || locked()) return;event.preventDefault();event.dataTransfer.dropEffect = 'move';row.classList.add('drop-target');};
      row.ondragleave = () => row.classList.remove('drop-target');
      row.ondrop = event => {if (sequenceDrag == null) return;event.preventDefault();event.stopPropagation();moveItem(sequenceDrag, index);sequenceDrag = null;};
      row.ondragend = () => {sequenceDrag = null;document.querySelectorAll('.dragging,.drop-target').forEach(n=>n.classList.remove('dragging','drop-target'));};
      row.append(controls);$('sequence').append(row);
    });
  }
  function renderSummary() {
    if (!draft) return;
    const fileType = (draft.outputFormat || 'mp4').toUpperCase();
    const n = draft.items.length, seconds = draft.items.reduce((sum, i) => sum + Math.min(i.totalTicks / 60, draft.sampleSeconds ?? Infinity), 0), isLocked = locked();
    $('nav-count').textContent = String(n).padStart(2, '0');
    $('export-indicator').hidden = !batch.running && !(singleJob && !singleJob.finished);
    $('no-jobs').hidden = !!batch.job || !!draft.signature || !!singleJob;
    $('sample-field').hidden = $('capture-length').value !== 'sample';
    $('sequence-duration').textContent = duration(seconds);
    $('sequence-size').textContent = `${plural(n, 'film')} · ${draft.sampleSeconds == null ? 'Full original films' : `First ${draft.sampleSeconds}s of each film`}`;
    const cleanName = (draft.name || 'Theater reel').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim().replace(/\.+$/, '').slice(0, 72) || 'Theater reel';
    const output = draft.signature ? draft.outputDirectory : `${batch.outputRoot || 'Downloads\\Theater Exports'}\\${cleanName}-${draft.id.slice(0, 8)}`;
    $('output-path').textContent = output; $('output-path').title = output;
    $('delivery-count').textContent = !n ? 'Choose your films' : draft.mode === 'separate' ? plural(n, 'individual ' + fileType) : draft.mode === 'combined' ? `1 ${fileType} · ${plural(n, 'chapter')}` : `${n + 1} ${fileType}s · ${n} individual + 1 reel`;
    $('delivery-description').textContent = `${draft.outputResolution || 1440}p${draft.frameRate || 60} · ${draft.outputQuality || 'high'} quality · FOV ${draft.fov}° · Game audio`;
    // Delivery plus the active video intermediate, cached audio and its temporary capture.
    const longest = Math.max(0, ...draft.items.map(i => Math.min(i.totalTicks / 60, draft.sampleSeconds ?? Infinity)));
    const videoRate = 50000000 * (draft.frameRate || 60) / 60;
    const gigabytes = ((draft.mode === 'separate' ? seconds : seconds * 2) * (videoRate + 192000) + longest * videoRate + (seconds + longest) * 192000) / 8 / 1e9;
    $('estimate').textContent = (draft.outputResolution || 1440) !== 1440 || (draft.outputQuality || 'high') !== 'high' ? 'File size varies with quality and scene detail.' : n ? `Approx. ${gigabytes < 1 ? `${Math.ceil(gigabytes * 1000)} MB` : `${gigabytes.toFixed(1)} GB`} disk space needed.` : '';
    $('order-note').textContent = draft.mode === 'separate' ? 'Films export in this order. Numbered filenames keep your sequence intact.' : 'This sequence becomes the chapter order in your reel.';
    $('delivery-visual').replaceChildren();
    if (draft.mode !== 'combined') for (let i = 0; i < Math.min(n, 7); i++) $('delivery-visual').append(el('i', null, 'delivery-chip'));
    if (draft.mode === 'both' && n) $('delivery-visual').append(el('span', '+', 'delivery-plus'));
    if (draft.mode !== 'separate' && n) {const joined = el('span', null, 'delivery-chip joined'); for (let i = 0; i < Math.min(n, 12); i++) joined.append(el('i'));$('delivery-visual').append(joined);}
    let issue = !n ? 'Add at least one film to start.' : !Number.isInteger(draft.fov) || draft.fov < 55 || draft.fov > 120 ? 'Choose a FOV from 55 to 120.' : draft.sampleSeconds != null && (!Number.isFinite(draft.sampleSeconds) || draft.sampleSeconds <= 0 || draft.sampleSeconds > 43200) ? 'Choose a positive sample length up to 43200 seconds.' : !batch.toolsReady ? 'Bundled export tools are missing. Extract the complete application package again.' : singleJob && !singleJob.finished ? 'A single-film recording is active.' : (draft.frameRate || 60) === 120 && !highlightMode() && draft.items.some(i => (i.title || 'halo3') !== 'halo3') ? '120 fps works with Halo 3 films only. Remove the other films or choose 60 fps.' : '';
    if (batch.running) issue = 'Export in progress.';
    const complete = draft.state === 'completed' && !!draft.signature;
    $('preflight').textContent = issue || (complete ? 'This job is complete. Start a new reel to export again.' : draft.signature ? 'Ready to resume.' : `${plural(n, 'film')} ready`);
    $('preflight').classList.toggle('ready', !issue);
    $('start-batch').disabled = !!issue || !!batch.running || busy || complete;
    $('start-batch').textContent = batch.running ? 'Export running…' : draft.signature ? 'Resume export' : `Start export${n ? ` · ${plural(n, 'film')}` : ''}`;
    for (const id of ['output-resolution','output-quality','output-fps','output-format','reel-name','batch-fov','capture-length','sample-seconds','continue-errors','output-folder','pick-batch','empty-add','add-all']) $(id).disabled = isLocked;
    for (const radio of document.querySelectorAll('input[name=mode]')) radio.disabled = isLocked;
    $('new-job').disabled = isLocked;
    renderHighlights();
    const resolution = draft.outputResolution || 1440, quality = draft.outputQuality || 'high';
    $('capture-resolution').textContent=resolution === 2160 ? `4K${draft.frameRate || 60} (upscaled)` : `${resolution}p${draft.frameRate || 60}`;
    $('output-fps').parentElement.hidden=false;
    $('capture-quality').textContent=quality === 'high' ? 'High quality' : 'Standard quality';
    $('output-resolution').title='';
    $('output-resolution').parentElement.hidden=false;
    if (highlights.running || highlights.scanning) {$('start-batch').disabled = true;$('hero-open').disabled = true;$('pick').disabled = true;}
    else $('pick').disabled = false;
    if (highlightMode()) {
      const selected = selectedHighlights(), valid = scanMatches();
      const blocked = !!issue || !valid || !selected.length || locked() || !batch.toolsReady || (singleJob && !singleJob.finished);
      $('start-batch').disabled = !!blocked;$('start-batch').textContent = highlights.running ? 'Export running - ' : `Export ${plural(selected.length,'highlight')}`;
      $('preflight').textContent = issue || (highlights.running ? 'Exporting selected highlights.' : !valid ? 'Find multikills for the current sequence and options.' : !selected.length ? 'Select at least one matching clip.' : `${plural(selected.length,'clip')} ready - ${highlightOptions().beforeSeconds}s lead-in - Original recorder POV`);
      $('delivery-count').textContent = draft.mode === 'separate' ? plural(selected.length,'clip') : draft.mode === 'combined' ? `1 reel  -  ${plural(selected.length,'chapter')}` : `${selected.length} clips + 1 reel`;
      $('sequence-duration').textContent = duration(selected.reduce((v,c)=>v+c.durationSeconds,0));
      $('sequence-size').textContent = `${n} source films - Multikill highlights`;
      $('order-note').textContent = 'Sources are scanned in this order. Within each source, clips follow recorded event order.';
      $('delivery-description').textContent = `${highlightFormatOptions().layout==='soft-focus'?'1080×1920 · Soft focus · 60 fps':`${resolution}p60`} · ${quality} quality · FOV ${draft.fov} · Game audio`;
      const portrait = highlightFormatOptions().layout === 'soft-focus';
      $('capture-resolution').textContent=portrait?'1080×1920 · 60 fps':resolution === 2160 ? '4K60 (upscaled)' : `${resolution}p60`;
      $('capture-quality').textContent=quality === 'high' ? 'High quality' : 'Standard quality';
      $('output-resolution').disabled=isLocked || portrait;
      $('output-resolution').parentElement.hidden=portrait;
      $('output-fps').parentElement.hidden=true; // highlight clips are always 60 fps
      $('output-resolution').title=portrait?'Soft focus uses a 1080×1920 portrait canvas.':'';
      $('estimate').textContent = '';
    }
    renderExportVisibility();
    $('export-indicator').hidden &&= !highlights.running;
    renderHero();
    if (highlights.running || highlights.scanning) $('hero-open').disabled = true;
  }
  function renderJob() {
    const job = batch.job || (draft?.signature ? draft : null); $('batch-job').hidden = !job; if (!job) return;
    $('job-state').textContent = (job.state || 'saved job').toUpperCase(); $('job-heading').textContent = job.name;
    $('job-message').textContent = job.message; const done = job.items.filter(i => i.state === 'completed').length;
    const batchProgress = job.state === 'completed' ? 1 : Number.isFinite(job.progress) ? Math.min(.99, job.progress) : Math.min(.99, (done + (job.activeProgress || 0)) / Math.max(1, job.items.length));
    $('batch-progress').value = batchProgress;
    $('batch-progress-text').textContent = progressText(job, batchProgress);
    $('batch-cancel').hidden = !batch.running; $('batch-retry').hidden = !!batch.running || !['failed','cancelled'].includes(job.state);
    $('batch-open-folder').disabled = !job.outputDirectory;
    $('job-items').replaceChildren(); job.items.forEach((item, index) => {const row = el('li'); row.append(el('span', String(index + 1).padStart(2, '0'), 'sequence-number'));const name = el('div');name.append(el('strong', item.name));if (item.error) name.append(el('small', item.error));row.append(name, el('span', item.state, `job-item-state ${item.state}`));$('job-items').append(row);});
    $('combined-file').textContent = job.combinedFile ? `Combined file: ${job.combinedFile}` : '';
    if (!batch.running && job.finished && draft.id === job.id) {draft = structuredClone(job);applyControlsLock();}
  }
  function applyControlsLock() {renderSequence();renderLibrary();}
  function showBackground(job) {
    singleJob = job; $('background-job').hidden = !job; if (!job) return;
    $('background-heading').textContent = job.complete ? 'MP4 saved' : job.cancelled ? 'Recording cancelled' : 'Background recording';
    $('background-status').textContent = job.message; $('background-file').textContent = job.file;
    const progress = $('background-progress'), value = job.complete ? 1 : Number.isFinite(job.progress) ? job.progress : job.totalTicks > 0 ? Math.max(0, Math.min(.99, job.tick / job.totalTicks)) : null;
    if (value == null) progress.removeAttribute('value'); else progress.value = value;
    $('background-progress-text').textContent = progressText(job, value);
    $('background-cancel').disabled = job.finished; $('background-open-folder').disabled = !job.file;
  }

  // Dismiss cards independently of export state, so drafts, retries and files stay intact.
  const dismissedExports = new Set();
  try { for (const key of JSON.parse(localStorage.getItem('dismissed-exports') || '[]')) dismissedExports.add(key); } catch {}
  function exportCards() {
    return [
      {panel:'batch-job', kind:'batch', job:batch.job || (draft?.signature ? draft : null), running:!!batch.running},
      {panel:'highlights-job', kind:'highlights', job:highlights.job, running:!!highlights.running},
      {panel:'background-job', kind:'single', job:singleJob, running:!!singleJob && !singleJob.finished}
    ].map(card => ({...card, key:card.kind + ':' + (card.job?.id || card.job?.checkpoint || card.job?.file || '')}));
  }
  function renderExportVisibility() {
    let visible = false, clearable = false;
    for (const card of exportCards()) {
      if (card.running && dismissedExports.delete(card.key)) {
        try { localStorage.setItem('dismissed-exports', JSON.stringify([...dismissedExports].slice(-200))); } catch {}
      }
      const show = !!card.job && !dismissedExports.has(card.key);
      $(card.panel).hidden = !show;
      visible ||= show;
      clearable ||= show && !card.running;
    }
    $('no-jobs').hidden = visible;
    $('clear-exports').disabled = !clearable;
  }
  $('clear-exports').onclick = () => {
    for (const card of exportCards()) if (card.job && !card.running) dismissedExports.add(card.key);
    try { localStorage.setItem('dismissed-exports', JSON.stringify([...dismissedExports].slice(-200))); } catch {}
    renderExportVisibility();
  };

  async function refresh() {
    const catalog = await native('catalog'); films = catalog.films; batch = catalog.batch;artwork = catalog.artwork || {};receiveHighlights(catalog.highlights || {});
    $('background-recording').checked = !!catalog.backgroundRecording;showBackground(catalog.job);applyDraft(batch.draft);
    if (!catalog.maps) $('status').textContent = 'Choose the MCC halo3/maps folder to enable export.';
  }
  function importFilms(result) {
    if (locked()) {error('Start a new reel before adding more films.');return;}
    const picked = result.films;
    for (const film of picked) if (!films.some(f => f.id === film.id)) films.push(film);
    if (picked.length) addFilms(picked);
    const issues = [...result.errors, ...picked.filter(f => !f.canDecode).map(f => `${f.name}: ${f.reason}`)];
    if (issues.length) $('status').textContent = `${picked.length ? $('status').textContent + ' ' : ''}Skipped: ${issues.join(' ')}`;
  }
  async function pickFilms() {if (locked()) return;try {importFilms(await native('batch-pick-films'));} catch (e) {error(e);}}
  let dragDepth = 0;
  const hasFiles = event => Array.from(event.dataTransfer?.types || []).includes('Files');
  function clearDrop() {dragDepth = 0;$('file-drop').hidden = true;}
  document.addEventListener('dragenter', event => {
    if (!hasFiles(event)) return;
    event.preventDefault();dragDepth++;
    $('file-drop').textContent = locked() ? 'Queue locked. Start a new reel to add films.' : 'Drop Theater .mov files to add them to your sequence';
    $('file-drop').hidden = false;
  });
  document.addEventListener('dragover', event => {
    if (!hasFiles(event)) return;
    event.preventDefault();event.dataTransfer.dropEffect = locked() ? 'none' : 'copy';
  });
  document.addEventListener('dragleave', event => {if (hasFiles(event) && --dragDepth <= 0) clearDrop();});
  document.addEventListener('dragend', clearDrop);
  document.addEventListener('drop', async event => {
    event.preventDefault();clearDrop();
    const dropped = Array.from(event.dataTransfer?.files || []);
    if (!dropped.length) return;
    if (locked()) {error('Queue locked. Start a new reel to add films.');return;}
    const reelId = draft.id;
    busy = true;renderSummary();renderLibrary();renderSequence();
    try {
      const result = await native('batch-drop-films', {}, dropped);
      busy = false;
      if (draft.id === reelId) importFilms(result);
    } catch (e) {error(e);} finally {busy = false;renderSummary();renderLibrary();renderSequence();}
  });
  $('search').oninput = renderLibrary;
  $('pick-batch').onclick = pickFilms; $('empty-add').onclick = pickFilms; $('add-all').onclick = () => addFilms(visibleFilms());
  $('reel-name').oninput = changed;
  for (const id of ['output-resolution','output-quality','output-fps','output-format','batch-fov','capture-length','sample-seconds','continue-errors']) $(id).onchange = () => {changed();renderSequence();if(id === 'capture-length' && highlightMode()) $('highlights-panel').scrollIntoView({block:'start'});};
  for (const radio of document.querySelectorAll('input[name=mode]')) radio.onchange = changed;
  $('output-folder').onclick = () => native('batch-output-folder').then(state => {
    batch = state;
    if (draft?.signature && !state.draft?.signature) applyDraft(state.draft); // Unstarted job was moved to the new folder.
    else if (draft?.signature) $('status').textContent = 'This job already saved films, so it keeps its folder. The new folder applies to your next reel.';
    renderJob();renderSummary();renderSequence();
  }).catch(error);
  $('new-job').onclick = () => {draft = fresh(); batch.job = null; $('status').textContent = '';applyDraft(draft);persist();};
  async function start(plan) {
    busy = true;clearTimeout(saveTimer);renderSummary();
    try {if (saving) await saving;batch = await native('batch-start', {plan});applyDraft(batch.job || plan);$('status').textContent = ''; $('batch-job').scrollIntoView({behavior:'smooth',block:'nearest'});} catch (e) {error(e);} finally {busy = false;renderSummary();renderLibrary();renderSequence();}
  }
  $('start-batch').onclick = () => {if(!draft.signature)readControls();if (highlightMode()) exportHighlights();else start(structuredClone(draft));};
  $('batch-retry').onclick = () => start(structuredClone(batch.job || draft));
  $('batch-cancel').onclick = async () => {try {$('batch-cancel').disabled = true;batch = await native('batch-cancel');renderJob();renderSummary();} catch (e) {error(e);} finally {$('batch-cancel').disabled = false;}};
  $('batch-open-folder').onclick = () => native('batch-open-folder').catch(error);
  $('pick').onclick = () => native('pick-film').catch(error);
  $('maps').onclick = () => native('pick-maps').then(refresh).catch(error);
  $('movies').onclick = () => native('pick-movies').then(refresh).catch(error);
  $('background-recording').onchange = event => {renderHero();native('background-recording-preference', {enabled:event.currentTarget.checked}).catch(error);};
  $('background-cancel').onclick = () => native('background-cancel').catch(error);
  $('background-open-folder').onclick = () => native('background-open-folder').catch(error);
  for (const id of ['browse-library','hero-browse','sequence-add','footer-library']) $(id).onclick = openLibrary;
  $('close-library').onclick = closeLibrary;
  $('library-dialog').addEventListener('click', event => {if (event.target === $('library-dialog')) {const rect=event.target.getBoundingClientRect();if(event.clientX<rect.left||event.clientX>rect.right||event.clientY<rect.top||event.clientY>rect.bottom)closeLibrary();}});
  for (const [id, target] of [['show-sequence','sequence-section'],['show-exports','exports-section']]) $(id).onclick = () => {document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('active',b.id===id));$(target).scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth',block:'start'});};
  $('hero-open').onclick = () => {const film=films.find(f=>f.path.toLowerCase()===selectedPath.toLowerCase());if(film)native('open-film',{filmId:film.id}).catch(error);};
  $('hero-add').onclick = () => {const film=films.find(f=>f.path.toLowerCase()===selectedPath.toLowerCase());if(film)addFilms([film]);};

  function highlightOptions() {
    return {minimumKills:Number($('highlight-minimum').value), exact:$('highlight-exact').checked, selection:$('highlight-scope').value, beforeSeconds:Number($('highlight-before').value), afterSeconds:Number($('highlight-after').value)};
  }
  function applyHighlightOptions(o) {
    o ||= {minimumKills:4, exact:false, selection:'all', beforeSeconds:3, afterSeconds:3};
    $('highlight-minimum').value = String(o.minimumKills);$('highlight-exact').checked = !!o.exact;$('highlight-scope').value = o.selection;
    $('highlight-before').value = o.beforeSeconds;$('highlight-after').value = o.afterSeconds;
  }
  function scanKey(paths, options) {return JSON.stringify([paths.map(p=>p.replaceAll('\\','/').toLowerCase()),options.minimumKills,!!options.exact,options.selection,options.beforeSeconds,options.afterSeconds]);}
  function scanMatches() {
    return !!highlights.scan && scanKey(highlights.scan.inputs || [],highlights.scan.options) === scanKey((draft?.items || []).map(i=>i.film), highlightOptions());
  }
  function selectedHighlights() {return scanMatches() ? (highlights.scan.clips || []).filter(c=>highlightSelection.has(c.id)) : [];}
  function receiveHighlights(value) {
    highlights = value || {};
    const key = JSON.stringify((highlights.scan?.clips || []).map(c=>[c.id,c.startTick,c.endTick]));
    if (key !== highlightResultKey) {highlightResultKey=key;highlightSelection=new Set((highlights.scan?.clips || []).map(c=>c.id));}
  }
  function highlightTime(ticks) {return (ticks/60).toFixed(2)+'s';}
  function populateHighlightDiagnostics() {
    if (highlightDiagnosticsRendered || !scanMatches()) return;
    for (const film of highlights.scan.films || []) {
      const entry = el('div', null, 'highlight-film-detail');
      const outcome = film.error || (film.state === 'duplicate' ? 'Duplicate film (already scanned)' : film.matches ? plural(film.matches,'match')+' - '+film.recorder : 'No matching multikills');
      entry.append(el('p',PathName(film.film)+': '+outcome,film.error?'failed':''));
      if (film.warnings?.length) {
        const warnings = el('details');warnings.append(el('summary',plural(film.warnings.length,'warning')));
        for (const warning of film.warnings) warnings.append(el('p',warning));
        entry.append(warnings);
      }
      $('highlights-films').append(entry);
    }
    highlightDiagnosticsRendered = true;
  }
  function renderHighlightScanSummary() {
    const valid = scanMatches(), films = valid ? highlights.scan.films || [] : [];
    const key = valid ? JSON.stringify(films) : '';
    if (key !== highlightDiagnosticsKey) {
      highlightDiagnosticsKey = key;highlightDiagnosticsRendered = false;
      $('highlights-films').replaceChildren();$('highlights-scan-details').open = false;
    }
    $('highlights-scan-details').hidden = !valid || !films.length;
    if (!valid) {
      $('highlights-scan-summary').textContent = highlights.scan ? 'The sequence or options changed. Find multikills again.' : '';
      return;
    }
    const matching = films.filter(f=>f.matches>0).length, empty = films.filter(f=>f.state==='no-matches').length;
    const duplicates = films.filter(f=>f.state==='duplicate').length, issues = films.filter(f=>f.error || f.state==='unsupported' || f.state==='failed').length;
    const warnings = films.reduce((n,f)=>n+(f.warnings?.length || 0),0);
    const counts = [plural(films.length,'film')+' scanned',plural(matching,'matching film'),empty+' with no matches'];
    if (duplicates) counts.push(plural(duplicates,'duplicate'));
    if (issues) counts.push(issues+' needing attention');
    if (warnings) counts.push(plural(warnings,'warning'));
    $('highlights-scan-summary').textContent = counts.join(' · ');
    $('highlights-scan-details-label').textContent = 'View film scan details'+(issues || warnings ? ` (${plural(issues,'issue')}, ${plural(warnings,'warning')})` : '');
    if ($('highlights-scan-details').open) populateHighlightDiagnostics();
  }
  function renderHighlights() {
    $('highlights-panel').hidden = !highlightMode();
    const active = !!highlights.scanning || !!highlights.running, disabled = active || busy || !!batch.running;
    renderHighlightFormat(disabled);
    for (const id of ['highlight-minimum','highlight-exact','highlight-scope','highlight-before','highlight-after']) $(id).disabled = disabled;
    $('highlights-find').disabled = disabled || !draft?.items.length;
    $('highlights-cancel-scan').hidden = !highlights.scanning;$('highlights-scan-progress').hidden = !highlights.scanning;
    $('highlights-scan-progress').value = highlights.scanProgress || 0;
    $('highlights-scan-message').textContent = highlights.scanMessage || 'Select films, then find multikills.';
    $('highlights-select-all').disabled = disabled || !scanMatches();$('highlights-select-none').disabled = disabled || !scanMatches();
    renderHighlightScanSummary();$('highlights-results').replaceChildren();
    if (scanMatches()) {
      for (const clip of highlights.scan.clips || []) {
        const row=el('div',null,'highlight-row'),label=el('label'),check=el('input');check.type='checkbox';check.checked=highlightSelection.has(clip.id);check.disabled=disabled;
        check.setAttribute('aria-label',clip.medal+' from '+clip.recorder+' at '+highlightTime(clip.kills[0].update));
        check.onchange=()=>{check.checked?highlightSelection.add(clip.id):highlightSelection.delete(clip.id);renderSummary();};
        const info=el('span');info.append(el('strong',clip.medal+'  -  '+clip.recorder+'  -  '+displayMap(clip.map, clip)));
        info.append(el('small',PathName(clip.film)+'  -  '+clip.killCount+' kills  -  '+clip.durationSeconds.toFixed(2)+'s clip'));
        info.append(el('small','Range '+highlightTime(clip.startTick)+'  -  '+highlightTime(clip.endTick)+(clip.shortLeadIn?'  -  Lead-in shortened at film start':'')));
        label.append(check,info);row.append(label);
        const format=el('button','Format clip');format.disabled=disabled;format.onclick=()=>{
          if(locked())return;
          $('highlight-format').value='soft-focus';selectCaptionClip(clip.id);changed();
          $('highlight-format-editor').scrollIntoView({block:'center'});$('highlight-text-above').focus();
        };row.append(format);
        const preview=el('button','Render preview');preview.disabled=disabled;preview.onclick=()=>exportHighlights([clip.id],true);row.append(preview);$('highlights-results').append(row);
      }
      const selected=selectedHighlights();
      $('highlights-cost').textContent=selected.length?plural(selected.length,'selected clip')+' · '+duration(selected.reduce((v,c)=>v+c.durationSeconds,0))+' total':'Select the clips to export.';
    } else $('highlights-cost').textContent='';
    const job=highlights.job;$('highlights-job').hidden=!job;
    if(job) {
      $('highlights-job-heading').textContent=job.name;$('highlights-job-message').textContent=job.message;
      $('highlights-cache-status').textContent=(job.cachedSources?`Reused captured footage for ${plural(job.cachedSources,'source')} — no Theater replay. `:'')+(job.footageCacheWarning || '')+(job.captureWarning?' '+job.captureWarning:'');
      $('highlights-job-progress').value=job.progress || 0;
      $('highlights-job-timing').textContent=Math.floor((job.progress || 0)*100)+'%'+(Number.isFinite(job.estimatedSecondsRemaining)?'  -  Current capture: '+duration(job.estimatedSecondsRemaining)+' remaining':'')+'  -  '+job.state;
      $('highlights-cancel').hidden=!highlights.running;$('highlights-retry').hidden=!!highlights.running || !['failed','cancelled','draft'].includes(job.state);
      $('highlights-folder').disabled=!job.outputDirectory;
      $('highlights-job-clips').replaceChildren();
      for(const clip of job.clips || []) {
        const row=el('li',clip.medal+'  -  '+clip.recorder+'  -  '+clip.state+(clip.error?'  -  '+clip.error:''));
        if(clip.state==='completed') {const play=el('button','Play clip');play.onclick=()=>native('highlights-open-clip',{clipId:clip.id}).catch(error);row.append(play);}
        $('highlights-job-clips').append(row);
      }
    }
  }
  function PathName(path) {return path.split(/[\\/]/).pop();}
  async function exportHighlights(ids,preview=false) {
    if(!scanMatches() || highlights.running || highlights.scanning)return;
    ids ||= selectedHighlights().map(c=>c.id);if(!ids.length)return;
    busy=true;renderSummary();
    try {
      receiveHighlights(await native('highlights-start',{ids,preview,name:preview?'Highlight preview':draft.name,mode:preview?'separate':draft.mode,fov:draft.fov,continueOnError:draft.continueOnError,...highlightCompositionRequest()}));
      $('highlights-job').scrollIntoView({behavior:'smooth',block:'nearest'});
    } catch(e) {error(e);} finally {busy=false;renderSummary();renderLibrary();renderSequence();}
  }
  $('highlights-find').onclick=async()=>{
    if(locked())return;readControls();
    try {receiveHighlights(await native('highlights-scan',{request:{films:draft.items.map(i=>i.film),options:highlightOptions()}}));}
    catch(e){error(e);}finally{renderSummary();renderLibrary();renderSequence();}
  };
  $('highlights-scan-details').ontoggle=()=>{if($('highlights-scan-details').open)populateHighlightDiagnostics();};
  for(const id of ['highlight-minimum','highlight-exact','highlight-scope','highlight-before','highlight-after']) $(id).onchange=changed;
  $('highlights-cancel-scan').onclick=()=>native('highlights-cancel-scan').catch(error);
  $('highlights-select-all').onclick=()=>{highlightSelection=new Set((highlights.scan?.clips || []).map(c=>c.id));renderSummary();};
  $('highlights-select-none').onclick=()=>{highlightSelection.clear();renderSummary();};
  $('highlights-cancel').onclick=()=>native('highlights-cancel').then(receiveHighlights).then(renderSummary).catch(error);
  $('highlights-retry').onclick=()=>native('highlights-retry').then(receiveHighlights).then(renderSummary).catch(error);
  $('highlights-folder').onclick=()=>native('highlights-open-folder').catch(error);

  function highlightFormatOptions() {
    return {layout:$('highlight-format').value || 'original',blur:Number($('highlight-blur').value),dim:Number($('highlight-dim').value),gameplayPosition:Number($('highlight-position').value)};
  }
  function applyHighlightFormat(format) {
    const f={layout:'original',blur:22,dim:40,gameplayPosition:48,...format};
    $('highlight-format').value=f.layout;$('highlight-blur').value=f.blur;$('highlight-dim').value=f.dim;$('highlight-position').value=f.gameplayPosition;
  }
  function highlightCompositionRequest() {
    return {outputResolution:Number($('output-resolution').value),outputQuality:$('output-quality').value,outputFormat:$('output-format').value,format:highlightFormatOptions(),captions:draft.highlightCaptions || {}};
  }
  function selectCaptionClip(id) {
    formatClipId=id;$('highlight-caption-clip').value=id;
    const caption=draft?.highlightCaptions?.[id] || {above:'',below:''};
    $('highlight-text-above').value=caption.above;$('highlight-text-below').value=caption.below;
    renderHighlightLayout();
  }
  function renderHighlightFormat(disabled) {
    const portrait=highlightFormatOptions().layout==='soft-focus';
    $('highlight-format-editor').hidden=!portrait;
    for(const id of ['highlight-format','highlight-blur','highlight-dim','highlight-position'])$(id).disabled=disabled;
    const clips=scanMatches() ? highlights.scan.clips || [] : [];
    $('highlight-caption-clip').replaceChildren();
    for(const clip of clips){const option=el('option',clip.medal+' · '+displayMap(clip.map, clip)+' · '+highlightTime(clip.startTick));option.value=clip.id;$('highlight-caption-clip').append(option);}
    if(!clips.some(c=>c.id===formatClipId))selectCaptionClip(clips[0]?.id || '');
    $('highlight-caption-clip').value=formatClipId;
    for(const id of ['highlight-caption-clip','highlight-text-above','highlight-text-below'])$(id).disabled=disabled || !clips.length;
    $('highlight-caption-note').textContent=clips.length?'Captions apply only to this clip. Blur and framing apply to the batch.':'Find multikills to add individual clip captions.';
    renderHighlightLayout();
  }
  function renderHighlightLayout() {
    const f=highlightFormatOptions(),clip=(highlights.scan?.clips || []).find(c=>c.id===formatClipId);
    const url=clip && art(clip.map),top=(1920*f.gameplayPosition/100-304)/1920*100;
    $('highlight-blur-value').textContent=f.blur+' px';$('highlight-dim-value').textContent=f.dim+'%';$('highlight-position-value').textContent=f.gameplayPosition+'%';
    $('highlight-format-backdrop').style.backgroundImage=url?`url("${url}")`:'none';$('highlight-format-backdrop').style.filter=`blur(${f.blur*.2}px)`;
    $('highlight-format-shade').style.background=`rgba(0,0,0,${f.dim/100})`;
    $('highlight-format-game').style.backgroundImage=url?`url("${url}")`:'none';$('highlight-format-game').style.top=top+'%';
    const above=$('highlight-format-above'),below=$('highlight-format-below');
    above.textContent=$('highlight-text-above').value;below.textContent=$('highlight-text-below').value;
    above.style.top='6.25%';above.style.height=Math.max(0,top-56/19.2-6.25)+'%';
    below.style.top=(top+664/19.2)+'%';below.style.height=Math.max(0,1690/19.2-top-664/19.2)+'%';
    // Fit text into the same two regions used by the export compositor.
    for(const [node,maxSize] of [[above,17.6],[below,10]]){
      let size=maxSize;node.style.fontSize=size+'px';
      while(node.scrollHeight>node.clientHeight && size>2){size-=.2;node.style.fontSize=size+'px';}
    }
  }
  for(const id of ['highlight-format','highlight-blur','highlight-dim','highlight-position']){
    $(id).oninput=()=>{if(!locked())changed();};
    $(id).onchange=()=>{if(!locked())changed();};
  }
  $('highlight-caption-clip').onchange=()=>selectCaptionClip($('highlight-caption-clip').value);
  for(const id of ['highlight-text-above','highlight-text-below'])$(id).oninput=()=>{
    if(locked() || !formatClipId)return;editableDraft();draft.highlightCaptions ||= {};
    draft.highlightCaptions[formatClipId]={above:$('highlight-text-above').value,below:$('highlight-text-below').value};changed();
  };

  refresh().catch(error);
})();
