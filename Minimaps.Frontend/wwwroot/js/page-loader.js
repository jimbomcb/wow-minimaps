// Module init from data-page-module/data-page-init
// data-page-init should return disposal to call on cleanup
class PageLoader {
    constructor() {
        this.pendingCleanup = null;
        this.init = false;
    }

    async loadPageModule() {
        try {
            await this.cleanup();

            const pageElement = document.querySelector('[data-page-module]');
            if (!pageElement)
                return;

            const { pageModule: modulePath, pageInit: initFunction } = pageElement.dataset;
            if (!modulePath || !initFunction) {
                console.warn('Missing module path/init function');
                return;
            }

            console.log(`Loading page module: ${modulePath}`);
            const module = await import(modulePath);
            const initFn = module[initFunction];
            if (typeof initFn !== 'function') {
                console.error(`Init function '${initFunction}' not found/not a function ('${modulePath}')`);
                return;
            }

            this.pendingCleanup = await initFn();
            if (this.pendingCleanup && typeof this.pendingCleanup !== 'function') {
                this.pendingCleanup = null;
            }

        } catch (error) {
            console.error('Failed to load page module:', error);
        }
    }

    async cleanup() {
        if (this.pendingCleanup) {
            try {
                if (typeof this.pendingCleanup === 'function') {
                    await this.pendingCleanup();
                }
            } catch (error) {
                console.error('Error during page cleanup:', error);
            } finally {
                this.pendingCleanup = null;
            }
        }
    }

    async initialize() {
        if (this.init)
            return;

        if (!window.Blazor) {
            console.error("No blazor");
            return;
        }

        console.log('Init page loader');
        this.init = true;
        await this.loadPageModule();
        window.Blazor.addEventListener('enhancedload', () => this.loadPageModule());
        window.addEventListener('beforeunload', () => this.cleanup());
    }
}

const pageLoader = new PageLoader();
pageLoader.initialize();