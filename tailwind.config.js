// Build-time config for the stylesheet. The site used to pull cdn.tailwindcss.com
// at runtime, which is Tailwind's Play CDN - explicitly not for production. It is
// a ~400 KB render-blocking JIT compiler that styles the page after first paint.
// Building here produces about 11 KB of plain CSS instead.
//
// content is the whole input: a class that appears nowhere in index.html will not
// exist in the output. The class names the tab handler toggles from JavaScript
// ('hidden', 'border-b-2', 'border-indigo-500', 'border-transparent',
// 'text-slate-400', 'text-white') are written as string literals in that file, so
// the scanner sees them and they survive. If a future change builds class names
// dynamically instead, it needs a safelist here.
module.exports = {
  content: ['./index.html'],
  theme: { extend: {} },
  plugins: [],
};
