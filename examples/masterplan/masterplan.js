/* The plan is a control surface. Only native receipts change confirmed values. */
(() => {
  'use strict';
  const data = window.masterplanData;
  const records = new Map(data.buildings.map(b => [b.id, {...b, confirmed: null}]));
  const nodes = new Map();
  const calls = new Map();
  const desired = new Map();
  const seen = new Set();
  let ready = false, querying = false, queried = false, busy = false;
  let flushTimer;
  let connectionGeneration;
  const $ = id => document.getElementById(id);
  const number = v => new Intl.NumberFormat('en-US', {maximumFractionDigits: 1}).format(v);

  function connection(state, label) {
    $('connection').dataset.state = state;
    $('connection').querySelector('span').textContent = label;
  }
  function message(title, detail) {
    $('state-title').textContent = title;
    $('state-detail').textContent = detail;
  }
  function refresh() {
    const confirmed = [...records.values()].filter(b => b.confirmed);
    for (const b of records.values()) {
      const n = nodes.get(b.id);
      n.input.disabled = !ready || !b.confirmed;
      const active = [...calls.values()].find(call => call.id === b.id);
      const requested = desired.get(b.id)?.floors ?? active?.floors ?? b.confirmed?.floors;
      n.value.textContent = requested ?? '—';
      n.floor.innerHTML = b.confirmed ? `${b.confirmed.floors}<small>FL</small>` : '—';
      if (requested) n.input.value = requested;
      if (b.confirmed) n.floorHeight.textContent=`80 floors · ${number(b.confirmed.floor_height)} m / floor`;
    }
    $('building-count').textContent = confirmed.length ? `${confirmed.length} / ${records.size}` : '—';
    $('gfa').textContent = confirmed.length ? `${number(confirmed.reduce((sum,b) => sum + b.width*b.depth*b.confirmed.floors,0))} m²` : '—';
    $('tallest').textContent = confirmed.length ? `${number(Math.max(...confirmed.map(b => b.confirmed.height)))} m` : '—';
  }
  function confirmed(record) {
    const b = records.get(record.building_id);
    if (!b || !Number.isInteger(record.floors) || record.floors < 1 || record.floors > 80 || !Number.isFinite(record.height) || record.height <= 0 || record.height/record.floors < 1 || record.height/record.floors > 10 || record.invalid_definition) return false;
    b.confirmed = {floors:record.floors,height:record.height,floor_height:record.height/record.floors};
    return true;
  }
  function submit(id, floors, retry=0) {
    const b = records.get(id);
    if (!ready || busy || !b.confirmed || floors === b.confirmed.floors) return;
    busy = true;
    connection('pending','Updating model');
    message(`${b.name} · ${floors} floors`, 'Waiting for the native model to confirm the geometry change.');
    try {
      const sequence = window.slateControl.set(`${id}.floors`, floors);
      calls.set(sequence,{id,floors,retry});
    } catch (error) { busy=false; message('Change not submitted',error.message); }
    refresh();
  }
  function flush() {
    if (!ready || busy || querying || !desired.size) return;
    const [id, next] = desired.entries().next().value;
    desired.delete(id);
    if (records.get(id).confirmed?.floors === next.floors) { flush(); return; }
    submit(id,next.floors,next.retry || 0);
  }
  function queue(id, floors, immediate=false) {
    desired.set(id,{floors,retry:0});
    clearTimeout(flushTimer);
    if (immediate) flush(); else flushTimer=setTimeout(flush,120);
  }
  for (const b of records.values()) {
    const building = document.createElement('div');
    building.className = 'building';
    building.style.cssText = `left:${100*b.x/data.site.width}%;bottom:${100*b.y/data.site.depth}%;width:${100*b.width/data.site.width}%;height:${100*b.depth/data.site.depth}%;--building:rgb(${b.color.join(',')})`;
    building.tabIndex = 0;
    building.setAttribute('aria-label',`${b.name}: ${b.use}. Focus to reveal floor control.`);
    building.innerHTML = `<div class="footprint"><span class="building-id">${b.id}</span><span class="floor-label">—</span><span class="building-name">${b.name}</span></div><div class="control"><div class="control-head"><label for="floor-${b.id}">${b.name}</label><output for="floor-${b.id}">—</output></div><input id="floor-${b.id}" type="range" min="1" max="80" step="1" value="${b.floors}" disabled aria-label="${b.name} floor count"><div class="control-foot"><span>1 floor</span><span>80 floors · 3.6 m / floor</span></div></div>`;
    const n = {input:building.querySelector('input'),value:building.querySelector('output'),floor:building.querySelector('.floor-label'),floorHeight:building.querySelector('.control-foot span:last-child')};
    n.input.addEventListener('input',() => { n.value.textContent = n.input.value; queue(b.id,Number(n.input.value)); });
    n.input.addEventListener('change',() => queue(b.id,Number(n.input.value),true));
    nodes.set(b.id,n);
    $('buildings').appendChild(building);
  }
  function nativeValue(result) {
    return result && result.status === 'command_result' ? result.value : null;
  }
  function receive(state) {
    if (state.connection_generation !== undefined && state.connection_generation !== connectionGeneration) {
      connectionGeneration=state.connection_generation;
      queried=false;querying=false;busy=false;calls.clear();desired.clear();
      for (const b of records.values()) b.confirmed=null;
    }
    ready = state.ready === true;
    for (const reply of state.replies || []) {
      if (seen.has(reply.sequence)) continue;
      seen.add(reply.sequence);
      const call = calls.get(reply.sequence);
      if (!call) continue;
      calls.delete(reply.sequence);
      const value = reply.status === 'completed' ? nativeValue(reply.result) : null;
      if (call.query) {
        querying=false;
        queried=true;
        if (value && Array.isArray(value.buildings)) {
          const counts=new Map();
          for (const record of value.buildings) counts.set(record.building_id,(counts.get(record.building_id)||0)+1);
          for (const record of value.buildings) if (counts.get(record.building_id)===1) confirmed(record);
          const count = [...records.values()].filter(b => b.confirmed).length;
          message(count === records.size ? 'Model synchronized' : 'Some buildings are unavailable',`${count} native building solids confirmed. Hover a footprint to change its height.`);
        } else { message('Model state unavailable',reply.message || 'The native model did not return building data.'); }
      } else {
        busy=false;
        if (value && value.complete === true && value.updated === true && value.building_id === call.id && value.floors === call.floors && confirmed(value)) {
          message(`${records.get(call.id).name} updated`,`${value.floors} floors · ${number(value.height)} m. The connected solid geometry has changed.`);
          $('receipt').textContent = `${call.id} · confirmed native change #${reply.sequence}`;
        } else if ((reply.status === 'busy' || /busy|pending/i.test(reply.message || '')) && call.retry < 12) {
          if (!desired.has(call.id)) desired.set(call.id,{floors:call.floors,retry:call.retry+1});
          message('Waiting for the model', 'The latest slider value will be retried after the current operation.');
        } else { message('Change was not confirmed',reply.message || 'Inspect the native model before retrying.'); }
      }
    }
    if (ready && !queried && !querying && window.slateControl) {
      querying=true;
      calls.set(window.slateControl.set('modelState',null),{query:true});
    }
    if (!ready) {
      queried=false;
      desired.clear();
      for (const b of records.values()) b.confirmed=null;
      message('Connect the model wire',state.message || 'Activate the model connected to this dashboard.');
    }
    connection(busy || querying ? 'pending' : ready ? 'ready' : 'waiting',busy ? 'Updating model' : querying ? 'Reading native model' : ready ? 'Native model connected' : 'Waiting for Slate');
    refresh();
    if (desired.size && !busy) { clearTimeout(flushTimer); flushTimer=setTimeout(flush,150); }
  }
  window.addEventListener('slate-control',event => receive(event.detail || {}));
  if (window.slateControl) receive(window.slateControl.state);
  refresh();
})();
