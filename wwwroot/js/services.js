class ServiceManager {
    constructor() {
        this.initAxiosInterceptors();
        this.updateStatus();
        // 每30秒更新一次状态
        setInterval(() => this.updateStatus(), 30000);
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

    async updateStatus() {
        try {
            const response = await axios.get('/api/dicom/status');
            const status = response.data;
            
            // 更新服务状态
            this.updateServices(status);
            
            // 更新系统状态
            if (status.system) {
                this.updateSystemStatus(status.system);
            }
        } catch (error) {
            handleError(error, '获取服务状态失败');
            this.setAllServicesUnknown();
        }
    }

    // 拆分更新服务状态的函数
    updateServices(status) {
        this.updateServiceStatus('storeScp', status.store);
        this.updateServiceStatus('worklistScp', status.worklist);
        this.updateServiceStatus('qrScp', status.qr);
        this.updateServiceStatus('printScp', status.print);
    }

    // 拆分更新系统状态的函数
    updateSystemStatus(systemStatus) {
        this.updateCpuStatus(systemStatus);
        this.updateMemoryStatus(systemStatus);
        this.updateSystemInfo(systemStatus);
        this.updateUptime(systemStatus);
    }

    // 添加错误处理
    updateServiceStatus(serviceId, status) {
        try {
            // 更新导航栏状态
            const element = document.getElementById(`${serviceId}-status`);
            if (element) {
                if (status.isRunning) {
                    element.className = 'badge bg-success';
                    element.textContent = '运行中';
                } else {
                    element.className = 'badge bg-danger';
                    element.textContent = '已停止';
                }
            }

            // 更新模态框状态
            const modalElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
            const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
            
            if (modalElement) {
                modalElement.className = `badge ${status.isRunning ? 'bg-success' : 'bg-danger'}`;
                modalElement.textContent = status.isRunning ? '运行中' : '已停止';
            }
            
            if (modalInfoElement) {
                modalInfoElement.textContent = `AET: ${status.aeTitle}  端口: ${status.port}`;
            }
        } catch (error) {
            console.error(`更新服务状态失败: ${serviceId}`, error);
        }
    }

    setAllServicesUnknown() {
        ['storeScp', 'worklistScp', 'qrScp', 'printScp'].forEach(serviceId => {
            // 更新导航栏状态
            const element = document.getElementById(`${serviceId}-status`);
            if (element) {
                element.className = 'badge bg-warning';
                element.textContent = '未知';
            }

            // 更新模态框状态
            const modalElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
            const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
            
            if (modalElement) {
                modalElement.className = 'badge bg-warning';
                modalElement.textContent = '未知';
            }
            
            if (modalInfoElement) {
                modalInfoElement.textContent = 'AET: - 端口: -';
            }
        });
    }

    // 更新CPU状态
    updateCpuStatus(systemStatus) {
        try {
            const cpuUsage = systemStatus.cpuUsage;
            
            // 更新导航栏CPU状态
            const cpuStatus = document.getElementById('cpuStatus');
            if (cpuStatus) {
                cpuStatus.textContent = `${cpuUsage.toFixed(1)}%`;
                cpuStatus.parentElement.className = `status-item mb-1 ${this.getCpuStatusClass(cpuUsage)}`;
            }

            // 更新模态框CPU状态
            const modalCpuStatus = document.getElementById('modalCpuStatus');
            const modalCpuProgress = document.getElementById('modalCpuProgress');
            if (modalCpuStatus && modalCpuProgress) {
                modalCpuStatus.textContent = `${cpuUsage.toFixed(1)}%`;
                modalCpuProgress.style.width = `${cpuUsage}%`;
                modalCpuProgress.className = `progress-bar ${this.getCpuProgressClass(cpuUsage)}`;
            }
        } catch (error) {
            console.error('更新CPU状态失败:', error);
        }
    }

    // 更新内存状态
    updateMemoryStatus(systemStatus) {
        try {
            const memoryUsage = systemStatus.processMemory;
            const systemMemoryPercent = systemStatus.systemMemoryPercent;
            
            // 更新导航栏内存状态
            const memoryStatus = document.getElementById('memoryStatus');
            if (memoryStatus) {
                memoryStatus.textContent = `${memoryUsage.toFixed(0)}MB`;
                memoryStatus.parentElement.className = `status-item ${this.getMemoryStatusClass(memoryUsage)}`;
            }

            // 更新模态框内存状态
            const modalSystemMemory = document.getElementById('modalSystemMemory');
            const modalSystemMemoryProgress = document.getElementById('modalSystemMemoryProgress');
            if (modalSystemMemory && modalSystemMemoryProgress) {
                modalSystemMemory.textContent = 
                    `${systemStatus.systemMemoryUsed.toFixed(0)}MB / ${systemStatus.systemMemoryTotal.toFixed(0)}MB (${systemMemoryPercent.toFixed(1)}%)`;
                modalSystemMemoryProgress.style.width = `${systemMemoryPercent}%`;
                modalSystemMemoryProgress.className = `progress-bar ${this.getMemoryProgressClass(systemMemoryPercent)}`;
            }

            // 更新进程内存
            const modalProcessMemory = document.getElementById('modalProcessMemory');
            if (modalProcessMemory) {
                modalProcessMemory.textContent = `${memoryUsage.toFixed(0)}MB`;
            }
        } catch (error) {
            console.error('更新内存状态失败:', error);
        }
    }

    // 更新系统信息
    updateSystemInfo(systemStatus) {
        try {
            const modalProcessorInfo = document.getElementById('modalProcessorInfo');
            const modalOsVersion = document.getElementById('modalOsVersion');
            
            if (modalProcessorInfo) {
                modalProcessorInfo.textContent = `${systemStatus.processorCount}核 ${systemStatus.cpuModel}`;
            }
            if (modalOsVersion) {
                modalOsVersion.textContent = systemStatus.platform;
            }
        } catch (error) {
            console.error('更新系统信息失败:', error);
        }
    }

    // 更新运行时间
    updateUptime(systemStatus) {
        try {
            const uptime = systemStatus.processStartTime;
            let uptimeText = '';
            if (uptime.days > 0) {
                uptimeText += `${uptime.days}天`;
            }
            if (uptime.hours > 0 || uptime.days > 0) {
                uptimeText += `${uptime.hours}小时`;
            }
            uptimeText += `${uptime.minutes}分钟`;

            const modalUptime = document.getElementById('modalUptime');
            if (modalUptime) {
                modalUptime.textContent = uptimeText;
            }
        } catch (error) {
            console.error('更新运行时间失败:', error);
        }
    }

    // 获取CPU状态样式类
    getCpuStatusClass(cpuUsage) {
        return cpuUsage > 80 ? 'cpu-danger' :
               cpuUsage > 50 ? 'cpu-warning' : 'cpu-normal';
    }

    // 获取CPU进度条样式类
    getCpuProgressClass(cpuUsage) {
        return cpuUsage > 80 ? 'bg-danger' :
               cpuUsage > 50 ? 'bg-warning' : 'bg-success';
    }

    // 获取内存状态样式类
    getMemoryStatusClass(memoryUsage) {
        return memoryUsage > 1024 ? 'memory-danger' :
               memoryUsage > 512 ? 'memory-warning' : 'memory-normal';
    }

    // 获取内存进度条样式类
    getMemoryProgressClass(memoryPercent) {
        return memoryPercent > 80 ? 'bg-danger' :
               memoryPercent > 60 ? 'bg-warning' : 'bg-success';
    }
}

// 初始化服务管理器
let serviceManager;
document.addEventListener('DOMContentLoaded', () => {
    serviceManager = new ServiceManager();
    
    // 系统状态击事件
    document.querySelector('.system-status-group').addEventListener('click', () => {
        const systemInfoModal = new bootstrap.Modal(document.getElementById('systemInfoModal'));
        systemInfoModal.show();
    });

    // 服务状态点击事件
    document.querySelector('.service-status-group').addEventListener('click', () => {
        const serviceStatusModal = new bootstrap.Modal(document.getElementById('serviceStatusModal'));
        serviceStatusModal.show();
    });
});
