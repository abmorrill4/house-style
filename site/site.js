// House Style site: tabs, the live Issues pane fragments, and section entrances. No dependencies.
(function () {
  'use strict';

  // ---- Issues pane fragments ------------------------------------------------------------------------------------
  // A fragment is a [data-ui] root (or a demo panel) that holds highlighted text ([data-hl]) and issue rows ([data-issue]).
  var noteShown = false;

  function setupPane(root) {
    var original = new Map();
    root.querySelectorAll('[data-hl]').forEach(function (el) { original.set(el, el.innerHTML); });
    var result = root.querySelector('[data-result]');
    var count = root.querySelector('[data-count]');
    var empty = root.querySelector('[data-empty]');
    var firstNote = root.querySelector('[data-firstnote]');

    function hl(id) { return root.querySelector('[data-hl="' + id + '"]'); }
    function open() { return root.querySelectorAll('[data-issue]:not(.resolved)').length; }
    function update() {
      var n = open();
      if (count) count.textContent = n === 1 ? '1 issue' : n + ' issues';
      if (empty) empty.hidden = n !== 0;
    }
    function say(text) {
      if (!result) return;
      var again = document.createElement('button');
      again.className = 'ui-btn'; again.dataset.act = 'reset'; again.textContent = 'Start again';
      result.textContent = text + ' ';
      result.appendChild(again);
      result.hidden = false;
    }
    function resolve(row, text) {
      row.classList.add('resolved');
      row.classList.remove('on');
      row.querySelector('.msg').textContent = text;
      update();
    }

    root.addEventListener('mouseover', function (e) {
      var row = e.target.closest('[data-issue]');
      if (!row || row.classList.contains('resolved')) return;
      var t = hl(row.dataset.issue);
      if (t && !t.classList.contains('done')) t.classList.add('on');
    });
    root.addEventListener('mouseout', function (e) {
      var row = e.target.closest('[data-issue]');
      if (!row || row.contains(e.relatedTarget)) return;
      var t = hl(row.dataset.issue);
      if (t && !t.dataset.keep) t.classList.remove('on');
    });

    root.addEventListener('click', function (e) {
      var btn = e.target.closest('[data-act]');
      if (!btn) return;
      var act = btn.dataset.act;
      if (act === 'reset') { reset(); return; }
      var row = btn.closest('[data-issue]');
      if (!row) return;
      var t = hl(row.dataset.issue);

      if (act === 'change') {
        var oldText = t.textContent;
        var newText = t.dataset.new || '';
        var del = document.createElement('del'); del.className = 'tc'; del.textContent = oldText;
        t.textContent = ''; t.appendChild(del);
        if (newText) { var ins = document.createElement('ins'); ins.className = 'tc'; ins.textContent = newText; t.appendChild(ins); }
        t.classList.add('done'); t.classList.remove('on');
        resolve(row, newText ? "Changed '" + oldText + "' to '" + newText + "'" : "Deleted '" + oldText + "'");
        if (firstNote && !noteShown) { firstNote.hidden = false; noteShown = true; }
        say('The change is in your topic as a tracked change. Accept it on the Review tab to keep it.');
      } else if (act === 'ignore') {
        t.classList.add('done'); t.classList.remove('on');
        resolve(row, 'Ignored');
        say('Ignored. The text stays as it is.');
      } else if (act === 'disable') {
        t.classList.add('done'); t.classList.remove('on');
        resolve(row, 'Rule disabled');
        say('House Style stops linting for this rule. Enable it again in Rules.');
      } else if (act === 'goto') {
        t.classList.add('on'); t.dataset.keep = '1';
        say('Go To selects the text in your topic. This rule has no automatic change, so you edit it.');
      } else if (act === 'rewrite') {
        t.classList.add('on'); t.dataset.keep = '1';
        say("Rewrite asks the AI for a version without the promise. It uses your key or your company's gateway.");
      }
    });

    function reset() {
      original.forEach(function (html, el) { el.innerHTML = html; el.classList.remove('done', 'on'); delete el.dataset.keep; });
      root.querySelectorAll('[data-issue]').forEach(function (row) {
        row.classList.remove('resolved');
        if (row.dataset.msg) row.querySelector('.msg').textContent = row.dataset.msg;
      });
      if (result) result.hidden = true;
      if (firstNote) firstNote.hidden = true;
      update();
    }
    root.querySelectorAll('[data-issue]').forEach(function (row) { row.dataset.msg = row.querySelector('.msg').textContent; });
    update();
  }

  var hero = document.getElementById('hero-ui');
  if (hero) setupPane(hero);
  document.querySelectorAll('[data-demo]').forEach(setupPane);

  // ---- Tabs (WAI-ARIA tabs pattern, automatic activation) ------------------------------------------------------
  var tablist = document.querySelector('[role="tablist"]');
  if (tablist) {
    var tabs = Array.prototype.slice.call(tablist.querySelectorAll('[role="tab"]'));
    var select = function (tab, focus) {
      tabs.forEach(function (t) {
        var on = t === tab;
        t.setAttribute('aria-selected', on ? 'true' : 'false');
        t.tabIndex = on ? 0 : -1;
        var panel = document.getElementById(t.getAttribute('aria-controls'));
        panel.hidden = !on;
        if (on) { panel.classList.remove('swap'); void panel.offsetWidth; panel.classList.add('swap'); }
      });
      if (focus) tab.focus();
    };
    tabs.forEach(function (tab, i) {
      tab.addEventListener('click', function () { select(tab, false); });
      tab.addEventListener('keydown', function (e) {
        var j = null;
        if (e.key === 'ArrowRight') j = (i + 1) % tabs.length;
        else if (e.key === 'ArrowLeft') j = (i - 1 + tabs.length) % tabs.length;
        else if (e.key === 'Home') j = 0;
        else if (e.key === 'End') j = tabs.length - 1;
        if (j !== null) { e.preventDefault(); select(tabs[j], true); }
      });
    });
  }

  // ---- Table views for the charts -------------------------------------------------------------------------------
  document.querySelectorAll('.table-btn').forEach(function (b) {
    b.addEventListener('click', function () {
      var box = b.parentElement; var open = !box.classList.contains('open');
      box.classList.toggle('open', open);
      b.setAttribute('aria-expanded', open ? 'true' : 'false');
      b.textContent = open ? 'Hide the table' : 'Show as a table';
    });
  });

  // ---- Section entrances ---------------------------------------------------------------------------------------
  var reveal = document.querySelectorAll('[data-reveal]');
  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) { if (en.isIntersecting) { en.target.classList.add('in'); io.unobserve(en.target); } });
    }, { rootMargin: '0px 0px 0px 0px' });
    reveal.forEach(function (el) { io.observe(el); });
  } else {
    reveal.forEach(function (el) { el.classList.add('in'); });
  }
})();
