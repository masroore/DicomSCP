class PrintManager {
    constructor() {
        this.allPrintJobs = [];  // 存储所有打印任务
        this.currentPage = 1;
        this.pageSize = 10;
        this.initializeEvents();
        this.loadPrintJobs();
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
    }

    async loadPrintJobs() {
        try {
            const response = await fetch('/api/print');
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取打印任务失败');
            }
            
            const jobs = await response.json();
            this.allPrintJobs = jobs.sort((a, b) => 
                new Date(b.createTime) - new Date(a.createTime)
            );
            this.searchPrintJobs();  // 使用搜索函数来显示数据
        } catch (error) {
            console.error('加载打印任务失败:', error);
            this.showToast('错误', '加载打印任务失败: ' + error.message, 'danger');
        }
    }

    searchPrintJobs() {
        const patientId = document.getElementById('searchPrintPatientId')?.value?.toLowerCase() || '';
        const patientName = document.getElementById('searchPrintPatientName')?.value?.toLowerCase() || '';
        const accessionNumber = document.getElementById('searchPrintAccessionNumber')?.value?.toLowerCase() || '';
        const status = document.getElementById('searchPrintStatus')?.value || '';
        const searchDate = document.getElementById('searchPrintDate')?.value || '';

        // 过滤数据
        const filteredJobs = this.allPrintJobs.filter(job => {
            const jobDate = job.createTime ? new Date(job.createTime).toISOString().split('T')[0] : '';
            return (!patientId || job.patientId?.toLowerCase().includes(patientId)) &&
                   (!patientName || job.patientName?.toLowerCase().includes(patientName)) &&
                   (!accessionNumber || job.accessionNumber?.toLowerCase().includes(accessionNumber)) &&
                   (!status || job.status === status) &&
                   (!searchDate || jobDate === searchDate);
        });

        // 更新分页信息并显示当前页数据
        this.currentPage = 1;
        this.updatePrintJobsTable(filteredJobs);
    }

    resetSearch() {
        document.getElementById('printSearchForm').reset();
        this.currentPage = 1;
        this.updatePrintJobsTable(this.allPrintJobs);
    }

    updatePrintJobsTable(jobs) {
        // 计算当前页的数据
        const start = (this.currentPage - 1) * this.pageSize;
        const end = start + this.pageSize;
        const pageJobs = jobs.slice(start, end);

        const tbody = document.getElementById('print-jobs-table-body');
        if (!tbody) return;

        tbody.innerHTML = pageJobs.map(job => `
            <tr>
                <td>${job.jobId}</td>
                <td>${job.patientName || ''}</td>
                <td>${job.patientId || ''}</td>
                <td>${job.accessionNumber || ''}</td>
                <td>${this.formatDateTime(job.createTime)}</td>
                <td>
                    <span class="badge ${this.getStatusBadgeClass(job.status)}">
                        ${this.formatStatus(job.status)}
                    </span>
                </td>
                <td>
                    ${this.getActionButtons(job)}
                </td>
            </tr>
        `).join('');

        // 更新分页信息
        this.updatePagination(jobs.length);
        // 更新作业统计
        this.updateJobStats(jobs);
    }

    updatePagination(total) {
        const totalPages = Math.ceil(total / this.pageSize);
        const start = (this.currentPage - 1) * this.pageSize + 1;
        const end = Math.min(this.currentPage * this.pageSize, total);

        document.getElementById('print-currentRange').textContent = total > 0 ? `${start}-${end}` : '0-0';
        document.getElementById('print-totalCount').textContent = total;
        document.getElementById('print-currentPage').textContent = this.currentPage;

        document.getElementById('print-prevPage').disabled = this.currentPage === 1;
        document.getElementById('print-nextPage').disabled = this.currentPage === totalPages || total === 0;
    }

    prevPage() {
        if (this.currentPage > 1) {
            this.currentPage--;
            const filteredJobs = this.getFilteredJobs();
            this.updatePrintJobsTable(filteredJobs);
        }
    }

    nextPage() {
        const total = this.getFilteredJobs().length;
        const totalPages = Math.ceil(total / this.pageSize);
        if (this.currentPage < totalPages) {
            this.currentPage++;
            const filteredJobs = this.getFilteredJobs();
            this.updatePrintJobsTable(filteredJobs);
        }
    }

    getFilteredJobs() {
        const patientId = document.getElementById('searchPrintPatientId')?.value?.toLowerCase() || '';
        const patientName = document.getElementById('searchPrintPatientName')?.value?.toLowerCase() || '';
        const accessionNumber = document.getElementById('searchPrintAccessionNumber')?.value?.toLowerCase() || '';
        const status = document.getElementById('searchPrintStatus')?.value || '';
        const searchDate = document.getElementById('searchPrintDate')?.value || '';

        const filteredJobs = this.allPrintJobs.filter(job => {
            const jobDate = job.createTime ? new Date(job.createTime).toISOString().split('T')[0] : '';
            return (!patientId || job.patientId?.toLowerCase().includes(patientId)) &&
                   (!patientName || job.patientName?.toLowerCase().includes(patientName)) &&
                   (!accessionNumber || job.accessionNumber?.toLowerCase().includes(accessionNumber)) &&
                   (!status || job.status === status) &&
                   (!searchDate || jobDate === searchDate);
        });

        return filteredJobs.sort((a, b) => 
            new Date(b.createTime) - new Date(a.createTime)
        );
    }

    getStatusBadgeClass(status) {
        switch (status) {
            case 'PENDING': return 'bg-warning text-dark';
            case 'PRINTING': return 'bg-primary text-white';
            case 'COMPLETED': return 'bg-success text-white';
            case 'FAILED': return 'bg-danger text-white';
            default: return 'bg-secondary text-white';
        }
    }

    formatStatus(status) {
        const statusMap = {
            'PENDING': '<span class="badge bg-warning text-dark fw-bold">待处理</span>',
            'PRINTING': '<span class="badge bg-primary text-white fw-bold">打印中</span>',
            'COMPLETED': '<span class="badge bg-success text-white fw-bold">已完成</span>',
            'FAILED': '<span class="badge bg-danger text-white fw-bold">失败</span>'
        };
        return statusMap[status] || status;
    }

    getActionButtons(job) {
        const buttons = [];
        
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
            acc[job.status] = (acc[job.status] || 0) + 1;
            return acc;
        }, {});

        const statsContainer = document.getElementById('print-jobs-stats');
        if (statsContainer) {
            statsContainer.innerHTML = `
                <span class="badge bg-secondary me-2">总数: ${jobs.length}</span>
                ${stats.PENDING ? `<span class="badge bg-warning me-2">待处理: ${stats.PENDING}</span>` : ''}
                ${stats.PRINTING ? `<span class="badge bg-primary me-2">打印中: ${stats.PRINTING}</span>` : ''}
                ${stats.COMPLETED ? `<span class="badge bg-success me-2">已完成: ${stats.COMPLETED}</span>` : ''}
                ${stats.FAILED ? `<span class="badge bg-danger me-2">失败: ${stats.FAILED}</span>` : ''}
            `;
        }
    }

    showToast(title, message, type = 'success') {
        const toast = document.getElementById('printToast');
        const toastTitle = document.getElementById('printToastTitle');
        const toastMessage = document.getElementById('printToastMessage');
        
        if (toast && toastTitle && toastMessage) {
            // 设置标题和消息
            toastTitle.textContent = title;
            toastMessage.textContent = message;
            
            // 根据类型设置样式
            toast.className = 'toast';
            switch (type) {
                case 'success':
                    toast.classList.add('bg-success', 'text-white');
                    break;
                case 'error':
                    toast.classList.add('bg-danger', 'text-white');
                    break;
                case 'warning':
                    toast.classList.add('bg-warning', 'text-dark');
                    break;
                default:
                    toast.classList.add('bg-light', 'text-dark');
            }
            
            // 创建Toast实例并设置选项
            const bsToast = new bootstrap.Toast(toast, {
                delay: 2000 // 2秒后自动关闭
            });
            bsToast.show();
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

            // 获取图像元素和加载提示
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
            const job = this.allPrintJobs.find(j => j.jobId === jobId);
            if (!job) {
                throw new Error('未找到任务');
            }

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

    // 创建详情模态框
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
                            <table class="table table-bordered table-striped table-hover mb-0">
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
        const fields = [
            { key: 'jobId', name: '任务ID' },
            { key: 'patientId', name: '患者ID' },
            { key: 'patientName', name: '患者姓名' },
            { key: 'accessionNumber', name: '检查号' },
            { key: 'callingAE', name: '调用方AE' },
            { key: 'status', name: '状态' },
            { key: 'filmSize', name: '胶片尺寸' },
            { key: 'filmOrientation', name: '胶片方向' },
            { key: 'filmLayout', name: '胶片布局' },
            { key: 'magnificationType', name: '放大类型' },
            { key: 'borderDensity', name: '边框密度' },
            { key: 'emptyImageDensity', name: '空图像密度' },
            { key: 'minDensity', name: '最小密度' },
            { key: 'maxDensity', name: '最大密度' },
            { key: 'trimValue', name: '裁剪值' },
            { key: 'configurationInfo', name: '配置信息' },
            { key: 'createTime', name: '创建时间' },
            { key: 'updateTime', name: '更新时间' }
        ];

        return fields.map(field => {
            const value = job[field.key];
            return `
                <tr>
                    <th class="table-light" style="width: 150px;">${field.name}</th>
                    <td>${this.formatFieldValue(field.key, value)}</td>
                </tr>
            `;
        }).join('');
    }

    // 删除打印任务
    async deletePrintJob(jobId) {
        if (!confirm('确定要删除这个打印任务吗？')) {
            return;
        }

        try {
            const response = await fetch(`/api/print/${jobId}`, {
                method: 'DELETE'
            });

            if (response.ok) {
                this.showToast('成功', '删除成功');
                await this.loadPrintJobs(); // 重新加载任务列表
            } else {
                throw new Error('删除失败');
            }
        } catch (error) {
            console.error('删除打印任务失败:', error);
            this.showToast('错误', '删除打印任务失败', 'error');
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
        if (value === null || value === undefined) {
            return '-';
        }
        
        if (key === 'status') {
            return this.formatStatus(value);
        }
        
        if (key.toLowerCase().includes('time')) {
            return new Date(value).toLocaleString();
        }
        
        return value;
    }
}

// 初始化打印管理器
let printManager;
document.addEventListener('DOMContentLoaded', () => {
    printManager = new PrintManager();
}); 