// LearnCards — kleine JS-Helfer (Theme-Umschaltung)
window.lcTheme = {
    isDark: function () {
        return document.documentElement.dataset.theme === 'dark';
    },
    get: function () {
        return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
    },
    set: function (theme) {
        document.documentElement.dataset.theme = theme;
        try { localStorage.setItem('lc-theme', theme); } catch (e) { /* privat-Modus */ }
    },
    getStored: function () {
        try { return localStorage.getItem('lc-theme') || ''; } catch (e) { return ''; }
    }
};
