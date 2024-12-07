class ConfigManager {
    constructor() {
        this.initAxiosInterceptors();
        this.init();
        this.helpModal = new bootstrap.Modal(document.getElementById('configHelpModal'));
    }

    initAxiosInterceptors() {
        axios.interceptors.response.use(
            response => response,
            error => {
                if (error.response && error.response.status === 401) {
                    window.location.href = '/login.html';
                    return new Promise(() => {});
                }
                return Promise.reject(error);
            }
        );
    }

    init() {
        // 监听页面切换事件
        document.querySelectorAll('.nav-link[data-page]').forEach(link => {
            link.addEventListener('click', (e) => {
                if (e.target.getAttribute('data-page') === 'settings') {
                    this.loadConfig();
                }
            });
        });

        // 如果当前在设置页面，加载配置
        if (window.location.hash === '#settings') {
            this.loadConfig();
        }
    }

    async loadConfig() {
        try {
            const response = await axios.get('/api/config');
            const editor = document.getElementById('configEditor');
            if (editor) {
                editor.value = JSON.stringify(response.data, null, 2);
            }
        } catch (error) {
            console.error('获取配置失败:', error);
            this.showToast('error', '获取配置失败: ' + error.message);
        }
    }

    async saveConfig() {
        try {
            // 先验证是否是有效的 JSON
            const configText = document.getElementById('configEditor').value;
            let config;
            try {
                config = JSON.parse(configText);
            } catch (e) {
                this.showToast('danger', '配置格式不正确，请检查JSON格式');
                return;
            }

            await axios.post('/api/config', config);

            this.showToast('success', '配置保存成功！需要重启服务生效');
        } catch (error) {
            console.error('保存配置失败:', error);
            this.showToast('danger', '保存配置失败: ' + error.message);
        }
    }

    showToast(type, message) {
        const toast = document.getElementById('storeToast');
        const toastTitle = document.getElementById('storeToastTitle');
        const toastMessage = document.getElementById('storeToastMessage');
        
        // 设置样式
        toast.classList.remove('bg-success', 'bg-danger', 'text-white');
        if (type === 'success') {
            toast.classList.add('bg-success', 'text-white');
            toastTitle.textContent = '操作成功';
        } else {
            toast.classList.add('bg-danger', 'text-white');
            toastTitle.textContent = '操作失败';
        }
        
        toastMessage.textContent = message;
        
        const bsToast = new bootstrap.Toast(toast);
        bsToast.show();
    }

    showHelp() {
        this.helpModal.show();
    }
}

// 初始化
window.configManager = new ConfigManager(); 

