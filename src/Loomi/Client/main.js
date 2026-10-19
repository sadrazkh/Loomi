import { createApp } from 'vue';
import App from './App.vue';
// Vazirmatn for every script and every weight the design uses; bundled, so the app never waits on a font host it may not reach.
import '@fontsource/vazirmatn/400.css';
import '@fontsource/vazirmatn/500.css';
import '@fontsource/vazirmatn/600.css';
import '@fontsource/vazirmatn/700.css';
import '@fontsource/vazirmatn/800.css';
import './styles/base.css';
createApp(App).mount('#app');
