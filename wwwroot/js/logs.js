class LogManager {
    constructor() {
        this.currentType = '';
        this.pageSize = 10;
        this.currentPage = 1;
        this.allFiles = [];
        this.init();
    }

    async init() {
        await this.loadLogTypes();
        this.bindEvents();
    }

    async loadLogTypes() {
        try {
            const response = await fetch('/api/logs/types');
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取日志类型失败');
            }
            
            const types = await response.json();
            this.renderLogTypes(types);
            if (types.length > 0) {
                await this.loadLogFiles(types[0]);
            }
        } catch (error) {
            console.error('加载日志类型失败:', error);
        }
    }

    async loadLogFiles(type) {
        try {
            this.currentType = type;
            const response = await fetch(`/api/logs/files/${type}`);
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error('获取日志文件失败');
            }
            
            this.allFiles = await response.json();
            this.renderLogFiles();
            this.updateActiveType();
            this.updatePagination();
        } catch (error) {
            console.error('加载日志文件失败:', error);
        }
    }

    renderLogTypes(types) {
        const container = document.getElementById('logTypes');
        container.innerHTML = types.map(type => `
            <a href="#" class="list-group-item list-group-item-action" data-type="${type}">
                ${type}
            </a>
        `).join('');
    }

    renderLogFiles() {
        const start = (this.currentPage - 1) * this.pageSize;
        const end = start + this.pageSize;
        const pageFiles = this.allFiles.slice(start, end);
        
        const container = document.getElementById('logFiles');
        const today = new Date().toISOString().split('T')[0];
        
        container.innerHTML = pageFiles.map(file => {
            const isToday = new Date(file.lastModified).toISOString().split('T')[0] === today;
            return `
                <tr>
                    <td>
                        <a href="#" onclick="logManager.viewLogContent('${file.name}'); return false;">
                            ${file.name}
                        </a>
                    </td>
                    <td>${this.formatFileSize(file.size)}</td>
                    <td>${new Date(file.lastModified).toLocaleString()}</td>
                    <td>
                        ${!isToday ? `
                            <button class="btn btn-danger btn-sm" onclick="logManager.deleteLogFile('${file.name}')">
                                删除
                            </button>
                        ` : ''}
                    </td>
                </tr>
            `;
        }).join('');
    }

    updateActiveType() {
        document.querySelectorAll('#logTypes .list-group-item').forEach(item => {
            item.classList.remove('active');
            if (item.dataset.type === this.currentType) {
                item.classList.add('active');
            }
        });
    }

    formatFileSize(bytes) {
        const units = ['B', 'KB', 'MB', 'GB'];
        let size = bytes;
        let unitIndex = 0;
        while (size >= 1024 && unitIndex < units.length - 1) {
            size /= 1024;
            unitIndex++;
        }
        return `${size.toFixed(2)} ${units[unitIndex]}`;
    }

    bindEvents() {
        document.getElementById('logTypes').addEventListener('click', e => {
            e.preventDefault();
            const type = e.target.dataset.type;
            if (type) {
                this.loadLogFiles(type);
            }
        });

        document.body.addEventListener('change', e => {
            if (e.target.id === 'logFileSelect') {
                this.loadLogContent(e.target.value);
            }
        });
    }

    async deleteLogFile(filename) {
        if (!confirm(`确定要删除日志文件 ${filename} 吗？`)) {
            return;
        }

        try {
            const response = await fetch(`/api/logs/${this.currentType}/${filename}`, {
                method: 'DELETE'
            });
            
            if (response.status === 401) {
                window.location.href = '/login.html';
                return;
            }
            
            if (!response.ok) {
                throw new Error(await response.text());
            }
            
            await this.loadLogFiles(this.currentType);
        } catch (error) {
            alert(error.message || '删除日志文件失败');
        }
    }

    async viewLogContent(filename) {
        try {
            const response = await fetch(`/api/logs/${this.currentType}/${filename}/content`);
            if (!response.ok) {
                throw new Error('获取日志内容失败');
            }
            
            const data = await response.json();
            
            const modalHtml = `
                <div class="modal fade" id="logContentModal" tabindex="-1">
                    <div class="modal-dialog modal-lg" style="max-width: 1000px;">
                        <div class="modal-content">
                            <div class="modal-header" style="background: white; position: sticky; top: 0; z-index: 1020;">
                                <div class="d-flex align-items-center">
                                    <select class="form-select form-select-sm me-2" id="logFileSelect" style="width: auto;">
                                        <!-- 日志文件列表将动态填充 -->
                                    </select>
                                </div>
                                <div>
                                    <button type="button" class="btn btn-outline-primary btn-sm me-1" onclick="logManager.refreshLogContent()">
                                        刷新
                                    </button>
                                    <button type="button" class="btn btn-outline-warning btn-sm me-1" onclick="logManager.clearLogContent()">
                                        清空
                                    </button>
                                    <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                                </div>
                            </div>
                            <div class="modal-body p-2" style="height: calc(100vh - 280px);">
                                <div style="height: 100%; border: 1px solid #dee2e6; border-radius: 4px; overflow: hidden;">
                                    <pre class="log-content" style="height: 100%; margin: 0; padding: 1rem; color: #d4d4d4; 
                                        font-family: Consolas, monospace; font-size: 0.9rem; line-height: 1.5;
                                        background-color: #1e1e1e; overflow-y: auto; overflow-x: hidden;
                                        white-space: pre-wrap;">${data.content.reverse().join('\n') || '暂无日志内容'}</pre>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            `;

            const existingModal = document.getElementById('logContentModal');
            if (existingModal) {
                existingModal.remove();
            }

            document.body.insertAdjacentHTML('beforeend', modalHtml);
            await this.updateLogFileSelect(filename);

            this.currentModal = new bootstrap.Modal(document.getElementById('logContentModal'), {
                backdrop: 'static',
                keyboard: false
            });
            this.currentModal.show();

        } catch (error) {
            console.error('获取日志内容失败:', error);
            alert('获取日志内容失败');
        }
    }

    async updateLogFileSelect(currentFile) {
        const response = await fetch(`/api/logs/files/${this.currentType}`);
        const files = await response.json();
        
        const select = document.getElementById('logFileSelect');
        select.innerHTML = files.map(file => `
            <option value="${file.name}" ${file.name === currentFile ? 'selected' : ''}>
                ${file.name}
            </option>
        `).join('');
    }

    async refreshLogContent() {
        const select = document.getElementById('logFileSelect');
        if (select) {
            await this.loadLogContent(select.value);
        }
    }

    async loadLogContent(filename) {
        try {
            const preElement = document.querySelector('.log-content');
            if (preElement) {
                // 显示简单的加载动画
                preElement.innerHTML = `
                    <div style="height: 100%; display: flex; align-items: center; justify-content: center;">
                        <div class="spinner-border text-secondary" role="status"></div>
                    </div>
                `;
            }

            const response = await fetch(`/api/logs/${this.currentType}/${filename}/content`);
            if (!response.ok) {
                throw new Error('获取日志内容失败');
            }
            
            const data = await response.json();
            
            if (preElement) {
                preElement.innerHTML = data.content.reverse().join('\n') || '暂无日志内容';
            }
        } catch (error) {
            console.error('加载日志内容失败:', error);
            alert('加载日志内容失败');
        }
    }

    async clearLogContent() {
        const select = document.getElementById('logFileSelect');
        if (select && confirm(`确定要清空日志文件 ${select.value} 吗？`)) {
            try {
                const response = await fetch(`/api/logs/${this.currentType}/${select.value}/clear`, {
                    method: 'POST'
                });
                
                if (response.status === 401) {
                    window.location.href = '/login.html';
                    return;
                }
                
                if (!response.ok) {
                    throw new Error('清空失败');
                }
                
                await this.refreshLogContent();
                await this.loadLogFiles(this.currentType);
            } catch (error) {
                alert(error.message || '清空日志文件失败');
            }
        }
    }

    updatePagination() {
        const totalPages = Math.ceil(this.allFiles.length / this.pageSize);
        const container = document.getElementById('logFiles-pagination');
        if (!container) return;

        container.innerHTML = `
            <div class="d-flex justify-content-between align-items-center mt-3">
                <div class="pagination-info">
                    显示 <span id="logs-currentRange">${(this.currentPage - 1) * this.pageSize + 1}-${Math.min(this.currentPage * this.pageSize, this.allFiles.length)}</span> 条，
                    共 <span id="logs-totalCount">${this.allFiles.length}</span> 条
                </div>
                <nav aria-label="分页导航">
                    <ul class="pagination mb-0">
                        <li class="page-item">
                            <button class="page-link" onclick="logManager.changePage(${this.currentPage - 1})" aria-label="上一页">
                                <span aria-hidden="true">&laquo;</span>
                            </button>
                        </li>
                        <li class="page-item">
                            <span class="page-link">${this.currentPage}</span>
                        </li>
                        <li class="page-item">
                            <button class="page-link" onclick="logManager.changePage(${this.currentPage + 1})" aria-label="下一页">
                                <span aria-hidden="true">&raquo;</span>
                            </button>
                        </li>
                    </ul>
                </nav>
            </div>
        `;
    }

    changePage(page) {
        const totalPages = Math.ceil(this.allFiles.length / this.pageSize);
        if (page >= 1 && page <= totalPages) {
            this.currentPage = page;
            this.renderLogFiles();
            this.updatePagination();
        }
    }
} 