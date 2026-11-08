// Prüft die reine Gestenauswertung aus wwwroot/cards.js (ohne Browser): node tests/browser/cards-gesture-unit.js
const c = require('../../src/LearnCards.Web/wwwroot/cards.js');
let bad = 0;
const eq = (n, a, b) => { if (a !== b) { bad++; console.log('FAIL', n, a, '!=', b); } else console.log('ok  ', n); };
eq('klein = pending', c.classify(3, 2), 'pending');
eq('horizontal = swipe', c.classify(-60, 8), 'swipe');
eq('vertikal = scroll', c.classify(5, 50), 'scroll');
eq('diagonal 45° = kein Swipe', c.classify(40, 40), 'scroll');
eq('flach diagonal weit = none', c.classify(50, 30), 'none');
eq('commit weit', c.shouldCommit(-90, 400, 360), true);
eq('commit kurz langsam nein', c.shouldCommit(-30, 800, 360), false);
eq('commit schneller Fling', c.shouldCommit(-40, 60, 360), true);
eq('tap', c.isTap(2, 3, 120), true);
eq('kein tap bei Bewegung', c.isTap(14, 0, 120), false);
eq('kein tap bei langem Druck', c.isTap(0, 0, 900), false);
process.exit(bad ? 1 : 0);
