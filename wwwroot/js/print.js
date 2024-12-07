class PrintManager {
    constructor() {
        this.initAxiosInterceptors();
        this.currentPage = 1;
        this.pageSize = 10;
        this.totalPages = 0;
        this.totalItems = 0;
        this.currentJobs = [];
        this.printers = [];
        this.selectedJobId = null;
        this.printerSelectModal = null;
        this.isLoading = false;

       // 初始化事件和加载数据
        this.init();
    }

    async init() {
        try {
            // 初始化事件绑定
            this.initializeEvents();
            
            // 初始化打印机选择模态框
            this.printerSelectModal = new bootstrap.Modal(document.getElementById('printerSelectModal'), {
                backdrop: 'static',
                keyboard: false
            });

            // 并行加载数据
            await Promise.all([
                this.loadPrinters(),
                this.loadPrintJobs()
            ]);
        } catch (error) {
            console.error('初始化打印管理器失败:', error);
            showToast('error', '初始化失败', '初始化打印管理器失败');
        }
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

    initializeEvents() {
        // 绑定刷新按钮事件
        const refreshBtn = document.getElementById('print-refresh');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => this.loadPrintJobs());
        }

        // 绑定分页按钮事件
        const prevBtn = document.getElementById('print-prevPage');
        const nextBtn = document.getElementById('print-nextPage');
        
        if (prevBtn) {
            prevBtn.addEventListener('click', () => this.prevPage());
        }
        if (nextBtn) {
            nextBtn.addEventListener('click', () => this.nextPage());
        }

        // 初始化打印机选择模态框
        this.printerSelectModal = new bootstrap.Modal(document.getElementById('printerSelectModal'), {
            backdrop: 'static',  // 点击背景不关闭
            keyboard: false      // 按ESC键不关闭
        });
        
        // 绑定确认打印按钮事件
        const confirmPrintBtn = document.getElementById('confirmPrint');
        if (confirmPrintBtn) {
            confirmPrintBtn.addEventListener('click', () => this.confirmPrint());
        }

        // 绑定取消按钮事件
        const cancelPrintBtn = document.getElementById('printerSelectModal').querySelector('.btn-secondary');
        if (cancelPrintBtn) {
            cancelPrintBtn.addEventListener('click', () => {
                this.selectedJobId = null;
                this.printerSelectModal.hide();
            });
        }

        // 绑定关闭按钮事件
        const closeBtn = document.getElementById('printerSelectModal').querySelector('.btn-close');
        if (closeBtn) {
            closeBtn.addEventListener('click', () => {
                this.selectedJobId = null;
                this.printerSelectModal.hide();
            });
        }

        // 添加搜索表单事件绑定
        const searchForm = document.getElementById('printSearchForm');
        if (searchForm) {
            // 查询按钮点击事件
            const searchBtn = searchForm.querySelector('button[type="button"]');
            if (searchBtn) {
                searchBtn.addEventListener('click', () => this.searchPrintJobs());
            }

            // 重置按钮点击事件
            const resetBtn = searchForm.querySelector('button.btn-secondary');
            if (resetBtn) {
                resetBtn.addEventListener('click', () => this.resetSearch());
            }
        }
    }

    async loadPrintJobs() {
        if (this.isLoading) {
            console.log('正在加载中，跳过重复请求');
            return;
        }

        const tbody = document.getElementById('print-jobs-table-body');
        if (!tbody) {
            console.error('找不到打印任务列表元素');
            return;
        }

        showTableLoading(tbody, 6);  // 打印列表有6列

        try {
            this.isLoading = true;

            // 获取搜索参数
            const searchParams = {
                callingAE: document.getElementById('searchCallingAE')?.value || '',
                studyUID: document.getElementById('searchStudyUID')?.value || '',
                status: document.getElementById('searchPrintStatus')?.value || '',
                date: document.getElementById('searchDate')?.value || '',
                page: this.currentPage,
                pageSize: this.pageSize
            };

            // 构建查询参数
            const queryParams = new URLSearchParams();
            if (searchParams.callingAE) queryParams.append('callingAE', searchParams.callingAE);
            if (searchParams.studyUID) queryParams.append('studyUID', searchParams.studyUID);
            if (searchParams.status) queryParams.append('status', searchParams.status);
            if (searchParams.date) queryParams.append('date', searchParams.date);
            queryParams.append('page', searchParams.page);
            queryParams.append('pageSize', searchParams.pageSize);

            const response = await axios.get(`/api/print?${queryParams}`);
            const data = response.data;

            if (!data.items || data.items.length === 0) {
                showEmptyTable(tbody, '暂无打印任务', 6);
                return;
            }

            // 更新数据和UI
            this.currentJobs = data.items;
            this.totalItems = data.total;
            this.totalPages = data.totalPages;
            this.updatePrintJobsTable(data.items);
            this.updatePagination();

        } catch (error) {
            console.error('加载打印任务失败:', error);
            showEmptyTable(tbody, '加载失败，请重试', 6);
        } finally {
            this.isLoading = false;
        }
    }

    updatePagination() {
        const start = (this.currentPage - 1) * this.pageSize + 1;
        const end = Math.min(this.currentPage * this.pageSize, this.totalItems);

        document.getElementById('print-currentRange').textContent = 
            this.totalItems > 0 ? `${start}-${end}` : '0-0';
        document.getElementById('print-totalCount').textContent = this.totalItems;
        document.getElementById('print-currentPage').textContent = this.currentPage;

        document.getElementById('print-prevPage').disabled = this.currentPage === 1;
        document.getElementById('print-nextPage').disabled = 
            this.currentPage === this.totalPages || this.totalItems === 0;
    }

    async prevPage() {
        if (this.currentPage > 1) {
            this.currentPage--;
            await this.loadPrintJobs();
        }
    }

    async nextPage() {
        if (this.currentPage < this.totalPages) {
            this.currentPage++;
            await this.loadPrintJobs();
        }
    }

    async searchPrintJobs() {
        this.currentPage = 1;
        await this.loadPrintJobs();
    }

    resetSearch() {
        const form = document.getElementById('printSearchForm');
        if (form) {
            form.reset();
            this.currentPage = 1;
            this.loadPrintJobs();
        }
    }

    updatePrintJobsTable(jobs) {
        const tbody = document.getElementById('print-jobs-table-body');
        if (!tbody) return;

        // 使用 DocumentFragment 提高性能
        const fragment = document.createDocumentFragment();
        jobs.forEach(job => {
            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td>${job.jobId || ''}</td>
                <td>${job.callingAE || ''}</td>
                <td>${job.studyInstanceUID || ''}</td>
                <td>${this.formatDateTime(job.createTime)}</td>
                <td>${this.formatStatus(job.status)}</td>
                <td>${this.getActionButtons(job)}</td>
            `;
            fragment.appendChild(tr);
        });

        tbody.innerHTML = '';
        tbody.appendChild(fragment);

        // 更新状态统计
        this.updateJobStats(jobs);
    }

    // 状态常量
    static PrintJobStatus = {
        0: 'Created',
        1: 'ImageReceived',
        2: 'Completed',
        3: 'Failed'
    };

    // 状态文本映射
    static StatusText = {
        'Created': '已创建',
        'ImageReceived': '已接收',
        'Completed': '已完成',
        'Failed': '失败'
    };

    formatStatus(status) {
        if (status === null || status === undefined) {
            return '<span class="badge bg-secondary">未知</span>';
        }

        // 先将数字状态转换为字符串状态
        const statusStr = PrintManager.PrintJobStatus[status] || status;
        // 再获取对应的中文文本
        const text = PrintManager.StatusText[statusStr] || statusStr;
        const badgeClass = this.getStatusBadgeClass(statusStr);
        return `<span class="badge ${badgeClass}">${text}</span>`;
    }

    getStatusBadgeClass(status) {
        switch (status) {
            case 'Created':
                return 'bg-secondary text-white';
            case 'ImageReceived':
                return 'bg-info text-white';
            case 'Completed':
                return 'bg-success text-white';
            case 'Failed':
                return 'bg-danger text-white';
            default:
                return 'bg-secondary text-white';
        }
    }

    getActionButtons(job) {
        const buttons = [];
        
        // 添加打印按钮
        buttons.push(`
            <button class="btn btn-sm btn-success me-1" 
                onclick="printManager.showPrinterSelect('${job.jobId}')">
                <i class="bi bi-printer"></i> 打印
            </button>
        `);

        // 添加预览按钮
        buttons.push(`
            <button class="btn btn-sm btn-primary me-1" 
                onclick="printManager.previewImage('${job.jobId}')">
                <i class="bi bi-eye"></i> 预览
            </button>
        `);

        // 添加详情按钮
        buttons.push(`
            <button class="btn btn-sm btn-info me-1" 
                onclick="printManager.showDetails('${job.jobId}')">
                <i class="bi bi-info-circle"></i> 详情
            </button>
        `);
        
        // 添加删除按钮
        buttons.push(`
            <button class="btn btn-sm btn-danger" 
                onclick="printManager.deletePrintJob('${job.jobId}')">
                <i class="bi bi-trash"></i> 删除
            </button>
        `);
        
        return buttons.join('');
    }

    formatDateTime(dateTimeStr) {
        if (!dateTimeStr) return '';
        const date = new Date(dateTimeStr);
        return date.toLocaleString('zh-CN', {
            year: 'numeric',
            month: '2-digit',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
    }

    updateJobStats(jobs) {
        const stats = jobs.reduce((acc, job) => {
            // 先将数字状态转换为字符串状态
            const statusStr = PrintManager.PrintJobStatus[job.status] || job.status;
            acc[statusStr] = (acc[statusStr] || 0) + 1;
            return acc;
        }, {});

        const statsContainer = document.getElementById('print-jobs-stats');
        if (statsContainer) {
            statsContainer.innerHTML = `
                <span class="badge bg-secondary me-2">总数: ${jobs.length}</span>
                ${stats['Created'] ? 
                    `<span class="badge bg-secondary me-2">已创建: ${stats['Created']}</span>` : ''}
                ${stats['ImageReceived'] ? 
                    `<span class="badge bg-info me-2">已接收: ${stats['ImageReceived']}</span>` : ''}
                ${stats['Completed'] ? 
                    `<span class="badge bg-success me-2">已完成: ${stats['Completed']}</span>` : ''}
                ${stats['Failed'] ? 
                    `<span class="badge bg-danger me-2">失败: ${stats['Failed']}</span>` : ''}
            `;
        }
    }

    showToast(type, message) {
        if (type === 'success' || type === '成功') {
            showSuccessMessage(message);
        } else {
            // 如果是错误消息，使用原始消息
            if (message instanceof Error) {
                handleError(message, '操作失败');
            } else {
                showToast('error', '操作失败', message);
            }
        }
    }

    // 预览图像
    async previewImage(jobId) {
        try {
            // 创建模态框（如果不存在）
            let modal = document.getElementById('previewModal');
            if (!modal) {
                modal = this.createPreviewModal();
            }

            // 显示模态框
            const bsModal = new bootstrap.Modal(modal);
            bsModal.show();

            // 获取图像元素和加载示
            const img = document.getElementById('previewImage');
            const loading = document.getElementById('imageLoading');

            // 重置图像
            img.style.opacity = '0';
            loading.style.display = 'block';

            // 设置图像源
            img.src = `/api/print/${jobId}/image?width=800`;

            // 图像加载完成后隐藏加载提示
            img.onload = () => {
                loading.style.display = 'none';
                img.style.opacity = '1';
            };

            // 图像加载失败处理
            img.onerror = () => {
                loading.style.display = 'none';
                this.showToast('错误', '图像加载失败', 'error');
                bsModal.hide();
            };
        } catch (error) {
            console.error('预览图像失败:', error);
            this.showToast('错误', '预览图像失败', 'error');
        }
    }

    // 创建预览模态框
    createPreviewModal() {
        const modal = document.createElement('div');
        modal.className = 'modal fade';
        modal.id = 'previewModal';
        modal.innerHTML = `
            <div class="modal-dialog modal-lg" style="max-width: 800px; margin: 1.75rem auto;">
                <div class="modal-content" style="background-color: #f8f9fa;">
                    <div class="modal-header py-2">
                        <h5 class="modal-title">图像预览</h5>
                        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                    </div>
                    <div class="modal-body p-0 text-center" style="background-color: #000;">
                        <div style="position: relative; width: 100%; height: 600px; display: flex; align-items: center; justify-content: center;">
                            <img id="previewImage" 
                                style="max-width: 100%; max-height: 100%; object-fit: contain;" 
                                alt="预览图像"
                                onload="this.style.opacity='1'"
                            />
                            <div id="imageLoading" class="position-absolute" style="color: #fff;">
                                <i class="bi bi-arrow-repeat spin"></i> 加载中...
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        `;

        // 添加加载动画样式
        const style = document.createElement('style');
        style.textContent = `
            @keyframes spin {
                from { transform: rotate(0deg); }
                to { transform: rotate(360deg); }
            }
            .spin {
                animation: spin 1s linear infinite;
                display: inline-block;
                font-size: 2rem;
            }
            #previewImage {
                opacity: 0;
                transition: opacity 0.3s ease-in-out;
            }
        `;
        document.head.appendChild(style);

        document.body.appendChild(modal);
        return modal;
    }

    // 显示任务详情
    async showDetails(jobId) {
        try {
            const response = await fetch(`/api/print/${jobId}`);
            if (!response.ok) {
                throw new Error('获取任务详情失败');
            }

            const job = await response.json();
            
            // 创建模态框（如果不存在）
            let modal = document.getElementById('detailModal');
            if (!modal) {
                modal = this.createDetailModal();
            }

            // 填充详情内容
            const tbody = document.getElementById('jobDetails');
            tbody.innerHTML = this.generateDetailRows(job);

            // 显示模态框
            const bsModal = new bootstrap.Modal(modal);
            bsModal.show();
        } catch (error) {
            console.error('获取任务详情失败:', error);
            this.showToast('错误', '获取任务详情失败', 'error');
        }
    }

    // 建详情模态框
    createDetailModal() {
        const modal = document.createElement('div');
        modal.className = 'modal fade';
        modal.id = 'detailModal';
        modal.innerHTML = `
            <div class="modal-dialog modal-lg" style="max-width: 800px;">
                <div class="modal-content">
                    <div class="modal-header py-2 bg-light" style="position: sticky; top: 0; z-index: 1050;">
                        <h5 class="modal-title">任务详情</h5>
                        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                    </div>
                    <div class="modal-body p-0">
                        <div class="table-responsive" style="max-height: 600px;">
                            <table class="table table-bordered mb-0">
                                <tbody id="jobDetails"></tbody>
                            </table>
                        </div>
                    </div>
                </div>
            </div>
        `;
        document.body.appendChild(modal);
        return modal;
    }

    // 生成详情行
    generateDetailRows(job) {
        const sections = [
            {
                title: '基本信息',
                fields: [
                    { key: 'jobId', name: '任务ID' },
                    { key: 'callingAE', name: '请求方AE' },
                    { key: 'status', name: '状态' },
                    { key: 'errorMessage', name: '错误信息' }
                ]
            },
            {
                title: 'Film Session 参数',
                fields: [
                    { key: 'numberOfCopies', name: '份数' },
                    { key: 'printPriority', name: '优先级' },
                    { key: 'mediumType', name: '介质类型' },
                    { key: 'filmDestination', name: '目标位置' }
                ]
            },
            {
                title: 'Film Box 参数',
                fields: [
                    { key: 'printInColor', name: '彩色印' },
                    { key: 'filmOrientation', name: '方向' },
                    { key: 'filmSizeID', name: '尺寸' },
                    { key: 'imageDisplayFormat', name: '显示格式' },
                    { key: 'magnificationType', name: '放大类型' },
                    { key: 'smoothingType', name: '平滑类型' },
                    { key: 'borderDensity', name: '边框密度' },
                    { key: 'emptyImageDensity', name: '空白密度' },
                    { key: 'trim', name: '裁剪' }
                ]
            },
            {
                title: '其他信息',
                fields: [
                    { key: 'studyInstanceUID', name: '检查UID' },
                    { key: 'imagePath', name: '图像路径' },
                    { key: 'createTime', name: '创建时间' },
                    { key: 'updateTime', name: '更新时间' }
                ]
            }
        ];

        return sections.map(section => `
            <tr>
                <th colspan="2" class="fw-bold" style="background-color: #f8f9fa; color: #495057; padding: 0.75rem;">
                    ${section.title}
                </th>
            </tr>
            ${section.fields.map(field => `
                <tr>
                    <th style="width: 150px; background-color: #fff;">${field.name}</th>
                    <td style="background-color: #fff;">${this.formatFieldValue(field.key, job[field.key])}</td>
                </tr>
            `).join('')}
        `).join('');
    }

    // 删除打印任务
    async deletePrintJob(jobId) {
        if (!await showConfirmDialog('确认删除', '确定要删除这个打印任务吗？')) {
            return;
        }

        try {
            const response = await fetch(`/api/print/${jobId}`, {
                method: 'DELETE'
            });

            if (!response.ok) {
                throw new Error('删除失败');
            }

            showSuccessMessage('打印任务已删除');
            await this.loadPrintJobs();
        } catch (error) {
            handleError(error, '删除失败');
        }
    }

    // 格式化字段名称
    formatFieldName(key) {
        const fieldNames = {
            jobId: '任务ID',
            filmSessionId: '胶片会话ID',
            filmBoxId: '胶片盒ID',
            callingAE: '调用方AE',
            status: '状态',
            imagePath: '图像路径',
            patientId: '患者ID',
            patientName: '患者姓名',
            accessionNumber: '检查号',
            filmSize: '胶片尺寸',
            filmOrientation: '胶片方向',
            filmLayout: '胶片布局',
            magnificationType: '放大类型',
            borderDensity: '边框密度',
            emptyImageDensity: '空图像密度',
            minDensity: '最小密度',
            maxDensity: '最大密度',
            trimValue: '裁剪值',
            configurationInfo: '配置信息',
            createTime: '创建时间',
            updateTime: '更新时间'
        };
        return fieldNames[key] || key;
    }

    // 格式化字段值
    formatFieldValue(key, value) {
        if (value === null || value === undefined) return '';
        
        switch (key) {
            case 'status':
                return this.formatStatus(value);
            case 'printInColor':
                return value ? '是' : '否';
            case 'createTime':
            case 'updateTime':
                return this.formatDateTime(value);
            default:
                return value.toString();
        }
    }

    // 加载打印机列表
    async loadPrinters() {
        try {
            const response = await axios.get('/api/PrintScu/printers');
            this.printers = response.data;
            this.updatePrinterSelect();
        } catch (error) {
            console.error('加载打印机失败:', error);
            this.showToast('错误', '加载打印机列表失败: ' + error.message, 'error');
        }
    }

    // 更新打印机下拉列表
    updatePrinterSelect() {
        const select = document.getElementById('printerSelect');
        if (!select) return;

        select.innerHTML = this.printers.map(printer => `
            <option value="${printer.name}" ${printer.isDefault ? 'selected' : ''}>
                ${printer.name} - ${printer.description || printer.aeTitle}
            </option>
        `).join('');

        // 如果没有选中的打印机（没有默认打印机），则选择第一个
        if (!select.value && this.printers.length > 0) {
            select.value = this.printers[0].name;
        }
    }

    // 显示打印机选择对话框
    showPrinterSelect(jobId) {
        this.selectedJobId = jobId;
        this.printerSelectModal.show();
    }

    // 确认打印
    async confirmPrint() {
        if (!this.selectedJobId) return;

        const printerSelect = document.getElementById('printerSelect');
        const printerName = printerSelect.value;

        if (!printerName) {
            this.showToast('error', '请选择打印机');
            return;
        }

        try {
            const response = await fetch('/api/PrintScu/print-by-job', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    jobId: this.selectedJobId,
                    printerName: printerName
                })
            });

            if (!response.ok) {
                throw new Error('打印失败');
            }

            await response.json();
            this.showToast('success', '打印任务已发送');
            this.printerSelectModal.hide();
            this.loadPrintJobs(); // 刷新任务列表
        } catch (error) {
            console.error('打印失败:', error);
            this.showToast('error', error.message || '打印失败');
        }
    }
}

// 初始化打印管理器
let printManager;
document.addEventListener('DOMContentLoaded', () => {
    printManager = new PrintManager();
}); 