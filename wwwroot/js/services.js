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
    
    // 系统状态和服务状态点击事件
    const systemStatusGroup = document.querySelector('.system-status-group');
    const serviceStatusGroup = document.querySelector('.service-status-group');
    const statusTip = document.querySelector('.status-tip');

    // 鼠标移入显示提示
    [systemStatusGroup, serviceStatusGroup].forEach(element => {
        element.addEventListener('mouseenter', (e) => {
            const rect = e.target.getBoundingClientRect();
            statusTip.style.top = `${rect.bottom + 5}px`;
            statusTip.style.left = `${rect.left}px`;
            statusTip.style.display = 'block';
        });

        element.addEventListener('mouseleave', () => {
            statusTip.style.display = 'none';
        });
    });

    // 点击事件
    systemStatusGroup.addEventListener('click', showSystemAndServiceStatus);
    serviceStatusGroup.addEventListener('click', showSystemAndServiceStatus);
});

// 显示系统和服务状态对话框
function showSystemAndServiceStatus() {
    return showDialog({
        title: '系统和服务状态',
        content: `
            <div class="mb-4">
                <h6 class="border-bottom pb-2">系统信息</h6>
                <div class="list-group">
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>CPU型号</strong>
                            <span id="modalCpuModel">Unknown</span>
                        </div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>CPU使用率</strong>
                            <span id="modalCpuUsage">0%</span>
                        </div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>系统内存</strong>
                            <span id="modalSystemMemory">0MB / 0MB (0%)</span>
                        </div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>程序内存</strong>
                            <span id="modalProcessMemory">0MB</span>
                        </div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>运行时间</strong>
                            <span id="modalUptime">0天0小时0分钟</span>
                        </div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between">
                            <strong>操作系统</strong>
                            <span id="modalOsVersion">Unknown</span>
                        </div>
                    </div>
                </div>
            </div>

            <div>
                <h6 class="border-bottom pb-2">DICOM 服务状态</h6>
                <div class="list-group">
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between align-items-center">
                            <strong>存储服务 (StoreSCP)</strong>
                            <span id="modalStoreScp" class="badge bg-secondary">加载中...</span>
                        </div>
                        <div class="small text-muted mt-1" id="modalStoreScpInfo">AET: - 端口: -</div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between align-items-center">
                            <strong>检查列表 (WorklistSCP)</strong>
                            <span id="modalWorklistScp" class="badge bg-secondary">加载中...</span>
                        </div>
                        <div class="small text-muted mt-1" id="modalWorklistScpInfo">AET: - 端口: -</div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between align-items-center">
                            <strong>查询服务 (QRSCP)</strong>
                            <span id="modalQrScp" class="badge bg-secondary">加载中...</span>
                        </div>
                        <div class="small text-muted mt-1" id="modalQrScpInfo">AET: - 端口: -</div>
                    </div>
                    <div class="list-group-item">
                        <div class="d-flex justify-content-between align-items-center">
                            <strong>打印服务 (PrintSCP)</strong>
                            <span id="modalPrintScp" class="badge bg-secondary">加载中...</span>
                        </div>
                        <div class="small text-muted mt-1" id="modalPrintScpInfo">AET: - 端口: -</div>
                    </div>
                </div>
            </div>
        `,
        showFooter: false,
        size: 'lg',
        onShow: () => {
            updateAllStatus();  // 更新所有状态
        }
    });
}

// 更新服务状态徽章
function updateServiceBadge(serviceId, isRunning) {
    const badge = document.getElementById(`${serviceId}-status`);
    const modalBadge = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}`);
    
    if (badge) {
        badge.className = `badge ${isRunning ? 'bg-success' : 'bg-danger'}`;
        badge.textContent = isRunning ? '运行中' : '已停止';
    }
    
    if (modalBadge) {
        modalBadge.className = `badge ${isRunning ? 'bg-success' : 'bg-danger'}`;
        modalBadge.textContent = isRunning ? '运行中' : '已停止';
    }
}

// 更新服务详细信息
function updateServiceInfo(serviceId, info) {
    const infoElement = document.getElementById(`${serviceId}Info`);
    const modalInfoElement = document.getElementById(`modal${serviceId.charAt(0).toUpperCase() + serviceId.slice(1)}Info`);
    
    const infoText = `AET: ${info.aeTitle} 端口: ${info.port}`;
    
    if (infoElement) {
        infoElement.textContent = infoText;
    }
    
    if (modalInfoElement) {
        modalInfoElement.textContent = infoText;
    }
}

// 更新所有状态
async function updateAllStatus() {
    try {
        const response = await axios.get('/api/dicom/status');
        const data = response.data;

        // 更新系统信息
        document.getElementById('modalCpuModel').textContent = data.system.cpuModel;
        document.getElementById('modalCpuUsage').textContent = `${data.system.cpuUsage}%`;
        document.getElementById('modalSystemMemory').textContent = 
            `${data.system.systemMemoryUsed}MB / ${data.system.systemMemoryTotal}MB (${data.system.systemMemoryPercent}%)`;
        document.getElementById('modalProcessMemory').textContent = `${data.system.processMemory}MB`;
        document.getElementById('modalUptime').textContent = 
            `${data.system.processStartTime.days}天${data.system.processStartTime.hours}小时${data.system.processStartTime.minutes}分钟`;
        document.getElementById('modalOsVersion').textContent = `${data.system.platform}`;

        // 更新导航栏状态显示
        document.getElementById('cpuStatus').textContent = `${data.system.cpuUsage}%`;
        document.getElementById('memoryStatus').textContent = `${data.system.processMemory}MB`;

        // 更新服务状态
        updateServiceBadge('storeScp', data.store.isRunning);
        updateServiceInfo('storeScp', data.store);
        updateServiceBadge('worklistScp', data.worklist.isRunning);
        updateServiceInfo('worklistScp', data.worklist);
        updateServiceBadge('qrScp', data.qr.isRunning);
        updateServiceInfo('qrScp', data.qr);
        updateServiceBadge('printScp', data.print.isRunning);
        updateServiceInfo('printScp', data.print);

    } catch (error) {
        console.error('获取状态信息失败:', error);
        window.showToast('获取状态信息失败', 'error');
    }
}
