// The 404 page: shows the documentation wording for a missing /docs/ address, and a search box that uses the
// documentation's own search index (Pagefind). Without the script, or if the index can't be loaded, the page still
// has its links. No dependencies.
(function () {
  'use strict';

  var inDocs = /^\/docs(\/|$)/.test(location.pathname);
  var site = document.querySelector('[data-404="site"]');
  var docs = document.querySelector('[data-404="docs"]');
  if (inDocs) { site.hidden = true; docs.hidden = false; document.title = 'Page not found · House Style documentation'; }

  var form = document.querySelector('[data-search]');
  var input = form.querySelector('input');
  var list = form.querySelector('[data-results]');
  var hint = document.querySelector('[data-nosearch]');
  var pagefind = null;
  var loading = null;
  var token = 0;

  function load() {
    if (!loading) {
      loading = import('/docs/pagefind/pagefind.js').then(function (pf) {
        pagefind = pf;
        return pf.options({ basePath: '/docs/pagefind/' });
      }).then(function () { return pagefind; });
    }
    return loading;
  }

  function show(items) {
    list.textContent = '';
    if (!items.length) {
      var none = document.createElement('li');
      none.textContent = 'Nothing found. Try other words.';
      list.appendChild(none);
      return;
    }
    items.forEach(function (d) {
      var li = document.createElement('li');
      var a = document.createElement('a');
      a.href = d.url;
      a.textContent = (d.meta && d.meta.title) || d.url;
      li.appendChild(a);
      if (d.excerpt) {
        var p = document.createElement('p');
        // The excerpt marks matches with <mark>. Everything else is shown as text.
        var tmp = document.createElement('div');
        tmp.innerHTML = d.excerpt;
        p.textContent = tmp.textContent;
        li.appendChild(p);
      }
      list.appendChild(li);
    });
  }

  function search() {
    var term = input.value.trim();
    var mine = ++token;
    if (term.length < 2) { list.textContent = ''; return; }
    load().then(function (pf) { return pf.search(term); }).then(function (res) {
      if (mine !== token) return;
      return Promise.all(res.results.slice(0, 8).map(function (r) { return r.data(); })).then(function (items) {
        if (mine === token) show(items);
      });
    }).catch(function () {
      // The index isn't available (for example, in a copy of the site that wasn't built with the documentation).
      form.hidden = true;
      hint.hidden = false;
    });
  }

  // Show the search box only when the index can be loaded.
  load().then(function () {
    form.hidden = false;
    hint.hidden = true;
    input.addEventListener('input', search);
    form.addEventListener('submit', function (e) { e.preventDefault(); search(); });
  }).catch(function () { /* keep the links */ });
})();
