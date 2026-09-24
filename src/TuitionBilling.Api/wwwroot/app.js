'use strict';

const store = {
  get token() { return localStorage.getItem('token'); },
  set token(value) { value ? localStorage.setItem('token', value) : localStorage.removeItem('token'); },
  get user() { try { return JSON.parse(localStorage.getItem('user') || 'null'); } catch { return null; } },
  set user(value) { value ? localStorage.setItem('user', JSON.stringify(value)) : localStorage.removeItem('user'); }
};

const money = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', minimumFractionDigits: 2 });
const rub = value => money.format(value);
const day = value => value ? new Date(value).toLocaleDateString('ru-RU') : '—';
const moment = value => value ? new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
// Согласование числительных: «1 начислению», «2 начислениям», «5 начислениям».
const plural = (count, one, few, many) => {
  const mod100 = Math.abs(count) % 100;
  const mod10 = mod100 % 10;
  if (mod100 > 10 && mod100 < 20) return many;
  if (mod10 === 1) return one;
  if (mod10 >= 2 && mod10 <= 4) return few;
  return many;
};

const escape = text => String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

async function api(path, options = {}) {
  const headers = Object.assign({ 'Content-Type': 'application/json' }, options.headers || {});
  if (store.token) headers.Authorization = `Bearer ${store.token}`;

  const response = await fetch(`/api${path}`, Object.assign({}, options, { headers }));
  if (response.status === 401) { logout(); throw new Error('Сессия истекла, войдите заново.'); }

  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.detail || body?.title || `Ошибка ${response.status}`);
  return body;
}

/* ---------- статусы ---------- */

const invoiceStatus = {
  Issued: ['Начислено', ''],
  PartiallyPaid: ['Оплачено частично', 'warn'],
  Paid: ['Оплачено', 'ok'],
  Canceled: ['Отменено', '']
};

const paymentStatus = {
  Pending: ['Ожидает оплаты', 'warn'],
  WaitingForCapture: ['Деньги захолдированы', 'warn'],
  Succeeded: ['Оплачен', 'ok'],
  Canceled: ['Отменён', 'bad']
};

const refundStatus = {
  Pending: ['В обработке', 'warn'],
  Succeeded: ['Возвращено', 'ok'],
  Canceled: ['Отменён', 'bad']
};

const tag = (map, key) => {
  const [text, cls] = map[key] || [key, ''];
  return `<span class="tag ${cls}">${escape(text)}</span>`;
};

/* ---------- каркас ---------- */

const el = id => document.getElementById(id);
let current = null;

const pages = {
  student: [
    { id: 'my-invoices', title: 'Мои начисления', render: renderMyInvoices },
    { id: 'my-payments', title: 'История платежей', render: renderMyPayments },
    { id: 'my-contracts', title: 'Мои договоры', render: renderMyContracts }
  ],
  accountant: [
    { id: 'contracts', title: 'Договоры и начисления', render: renderContracts },
    { id: 'payments', title: 'Платежи и возвраты', render: renderPayments },
    { id: 'ledger', title: 'Журнал проводок', render: renderLedger },
    { id: 'reconciliation', title: 'Сверка со шлюзом', render: renderReconciliation },
    { id: 'students', title: 'Обучающиеся', render: renderStudents }
  ]
};

function banner(text, kind) {
  const node = el('banner');
  if (!text) { node.hidden = true; return; }
  node.textContent = text;
  node.className = `banner ${kind || ''}`;
  node.hidden = false;
}

function role() {
  return store.user?.roles?.includes('accountant') ? 'accountant' : 'student';
}

function buildNav() {
  const nav = el('nav');
  nav.innerHTML = '';
  for (const page of pages[role()]) {
    const button = document.createElement('button');
    button.textContent = page.title;
    button.onclick = () => { banner(null); open(page.id); };
    button.dataset.page = page.id;
    nav.appendChild(button);
  }
}

async function open(pageId) {
  const page = pages[role()].find(p => p.id === pageId) || pages[role()][0];
  current = page.id;
  el('page-title').textContent = page.title;
  el('page-actions').innerHTML = '';
  el('content').innerHTML = '<div class="empty">Загрузка…</div>';
  document.querySelectorAll('#nav button').forEach(b => b.classList.toggle('active', b.dataset.page === page.id));

  try {
    await page.render();
  } catch (error) {
    el('content').innerHTML = `<div class="panel"><div class="error">${escape(error.message)}</div></div>`;
  }
}

const refresh = () => open(current);

/* ---------- кабинет обучающегося ---------- */

async function renderMyInvoices() {
  const invoices = await api('/invoices');
  const due = invoices.filter(i => i.outstanding > 0 && i.status !== 'Canceled');
  const total = due.reduce((sum, i) => sum + i.outstanding, 0);
  const overdue = due.filter(i => i.isOverdue).length;

  el('content').innerHTML = `
    <div class="cards" style="margin-bottom:22px">
      <div class="card"><h3>К оплате</h3><div class="sum">${rub(total)}</div><div class="muted">по ${due.length} ${plural(due.length, 'начислению', 'начислениям', 'начислениям')}</div></div>
      <div class="card"><h3>Просрочено</h3><div class="sum">${overdue}</div><div class="muted">${overdue ? plural(overdue, 'начисление просрочено', 'начисления просрочено', 'начислений просрочено') : 'просрочек нет'}</div></div>
    </div>
    <div class="panel">
      <h2>Начисления за учебные периоды</h2>
      ${invoices.length ? table(invoices) : '<div class="empty">Начислений пока нет.</div>'}
    </div>`;

  document.querySelectorAll('[data-pay]').forEach(button => {
    button.onclick = () => payInvoice(invoices.find(i => i.id === button.dataset.pay));
  });

  function table(rows) {
    return `<table>
      <thead><tr>
        <th>Период</th><th>Договор</th><th>Срок</th>
        <th class="num">Сумма</th><th class="num">Остаток</th><th>Статус</th><th></th>
      </tr></thead>
      <tbody>${rows.map(invoiceRow).join('')}</tbody>
    </table>`;
  }

  function invoiceRow(invoice) {
    const plan = invoice.installments.length
      ? `<details><summary>Рассрочка: ${invoice.installments.length} ${plural(invoice.installments.length, 'платёж', 'платежа', 'платежей')}</summary>
           <div class="entries">${invoice.installments.map(i =>
             `<div><span>Платёж ${i.number} до ${day(i.dueOn)}</span><span>${rub(i.amount)} ${i.isPaid ? '&#10004;' : ''}</span></div>`).join('')}</div>
         </details>`
      : '';

    return `<tr>
      <td>${escape(invoice.periodCode)}${plan}</td>
      <td class="muted">${escape(invoice.contractNumber)}</td>
      <td>${day(invoice.dueOn)}${invoice.isOverdue ? ' <span class="tag bad">просрочено</span>' : ''}</td>
      <td class="num">${rub(invoice.amount)}</td>
      <td class="num">${rub(invoice.outstanding)}</td>
      <td>${tag(invoiceStatus, invoice.status)}</td>
      <td class="num">${invoice.outstanding > 0 && invoice.status !== 'Canceled'
        ? `<button data-pay="${invoice.id}">Оплатить</button>` : ''}</td>
    </tr>`;
  }
}

function payInvoice(invoice) {
  showModal(`
    <h2>Оплата обучения</h2>
    <p class="muted">${escape(invoice.contractNumber)}, период ${escape(invoice.periodCode)}</p>
    <label>Сумма платежа, &#8381;
      <input type="number" id="pay-amount" step="0.01" min="0.01" max="${invoice.outstanding}" value="${invoice.outstanding}">
    </label>
    <p class="muted" style="font-size:13px">К оплате по начислению: ${rub(invoice.outstanding)}. Можно внести часть.</p>
    <div class="error" id="pay-error" hidden></div>
    <button id="pay-go">Перейти к оплате</button>`);

  el('pay-go').onclick = async () => {
    const amount = Number(el('pay-amount').value);
    const button = el('pay-go');
    button.disabled = true;
    try {
      const result = await api('/payments', {
        method: 'POST',
        // Ключ идемпотентности: если запрос повторится из-за обрыва связи
        // или двойного клика, второй платёж не создастся.
        headers: { 'Idempotence-Key': crypto.randomUUID() },
        body: JSON.stringify({ invoiceId: invoice.id, amount })
      });
      location.href = result.confirmationUrl;
    } catch (error) {
      const box = el('pay-error');
      box.textContent = error.message;
      box.hidden = false;
      button.disabled = false;
    }
  };
}

async function renderMyPayments() {
  const payments = await api('/payments');
  el('content').innerHTML = `<div class="panel">
    <h2>История операций</h2>
    <div class="sub">Квитанция доступна по успешному платежу.</div>
    ${payments.length ? `<table>
      <thead><tr><th>Дата</th><th>Период</th><th class="num">Сумма</th><th>Статус</th><th>Чек</th><th></th></tr></thead>
      <tbody>${payments.map(p => `<tr>
        <td>${moment(p.createdAt)}</td>
        <td>${escape(p.periodCode)}</td>
        <td class="num">${rub(p.amount)}${p.refundedAmount > 0 ? `<br><span class="muted" style="font-size:12px">возвращено ${rub(p.refundedAmount)}</span>` : ''}</td>
        <td>${tag(paymentStatus, p.status)}${p.cancellationReason ? `<br><span class="muted" style="font-size:12px">${escape(p.cancellationReason)}</span>` : ''}</td>
        <td class="muted">${p.fiscalDocumentNumber ? '&#8470; ' + escape(p.fiscalDocumentNumber) : '—'}</td>
        <td class="num">${p.status === 'Succeeded' ? `<button class="ghost" data-receipt="${p.id}">Квитанция</button>` : ''}</td>
      </tr>`).join('')}</tbody></table>` : '<div class="empty">Платежей пока нет.</div>'}
  </div>`;

  document.querySelectorAll('[data-receipt]').forEach(b => b.onclick = () => showReceipt(b.dataset.receipt));
}

async function showReceipt(paymentId) {
  const receipt = await api(`/payments/${paymentId}/receipt`);
  showModal(`<div class="receipt">
    <h2>Квитанция об оплате</h2>
    <div class="org">${escape(receipt.organizationName)}</div>
    <dl>
      <dt>Плательщик</dt><dd>${escape(receipt.studentName)}</dd>
      <dt>Договор</dt><dd>${escape(receipt.contractNumber)}</dd>
      <dt>Направление</dt><dd>${escape(receipt.programName)}</dd>
      <dt>Период обучения</dt><dd>${escape(receipt.periodCode)}</dd>
      <dt>Дата платежа</dt><dd>${moment(receipt.paidAt)}</dd>
      <dt>Операция шлюза</dt><dd>${escape(receipt.gatewayPaymentId || '—')}</dd>
      <dt>Кассовый чек</dt><dd>${receipt.fiscalDocumentNumber ? '&#8470; ' + escape(receipt.fiscalDocumentNumber) : 'формируется'}</dd>
    </dl>
    <div class="total">${rub(receipt.amount)}</div>
    <p class="muted" style="font-size:12.5px">Кассовый чек по 54-ФЗ формирует платёжный провайдер и отправляет на почту плательщика.</p>
    <button class="ghost no-print" onclick="window.print()">Распечатать</button>
  </div>`);
}

async function renderMyContracts() {
  const contracts = await api('/contracts');
  el('content').innerHTML = contracts.map(c => `<div class="panel">
    <h2>${escape(c.number)}</h2>
    <div class="sub">${escape(c.programName)} &middot; приём ${c.admissionYear} года &middot; договор от ${day(c.signedOn)}</div>
    <div class="cards" style="margin-bottom:14px">
      <div class="card"><h3>Стоимость обучения</h3><div class="sum">${rub(c.totalAmount)}</div></div>
      <div class="card"><h3>Начислено</h3><div class="sum">${rub(c.issuedAmount)}</div></div>
      <div class="card"><h3>Остаток к оплате</h3><div class="sum">${rub(c.outstandingAmount)}</div></div>
    </div>
  </div>`).join('') || '<div class="empty">Договоров нет.</div>';
}

/* ---------- кабинет бухгалтера ---------- */

async function renderContracts() {
  const [contracts, students] = await Promise.all([api('/contracts'), api('/students')]);

  el('page-actions').innerHTML = '<button id="new-contract">Новый договор</button>';
  el('new-contract').onclick = () => newContractForm(students);

  el('content').innerHTML = contracts.map(contract => `<div class="panel">
    <h2>${escape(contract.number)} &mdash; ${escape(contract.studentName)}</h2>
    <div class="sub">${escape(contract.programName)} &middot; стоимость ${rub(contract.totalAmount)} &middot;
      начислено ${rub(contract.issuedAmount)} &middot; остаток ${rub(contract.outstandingAmount)}</div>
    ${contract.invoices.length ? `<table>
      <thead><tr><th>Период</th><th>Срок</th><th class="num">Сумма</th><th class="num">Оплачено</th><th>Статус</th><th></th></tr></thead>
      <tbody>${contract.invoices.map(i => `<tr>
        <td>${escape(i.periodCode)}${i.installments.length ? ` <span class="tag">рассрочка &times;${i.installments.length}</span>` : ''}</td>
        <td>${day(i.dueOn)}</td>
        <td class="num">${rub(i.amount)}</td>
        <td class="num">${rub(i.paidAmount)}</td>
        <td>${tag(invoiceStatus, i.status)}</td>
        <td class="num">${i.status !== 'Canceled' && !i.installments.length && i.paidAmount === 0
          ? `<button class="ghost" data-split="${i.id}">Рассрочка</button>` : ''}</td>
      </tr>`).join('')}</tbody></table>` : '<div class="empty">Начислений нет.</div>'}
    <div class="row" style="margin-top:14px">
      <button class="ghost" data-invoice="${contract.id}">Выставить начисление</button>
    </div>
  </div>`).join('') || '<div class="empty">Договоров нет.</div>';

  document.querySelectorAll('[data-invoice]').forEach(b => b.onclick = () => issueInvoiceForm(b.dataset.invoice));
  document.querySelectorAll('[data-split]').forEach(b => b.onclick = () => splitForm(b.dataset.split));
}

function newContractForm(students) {
  showModal(`<h2>Новый договор</h2>
    <label>Обучающийся
      <select id="c-student">${students.map(s => `<option value="${s.id}">${escape(s.fullName)}</option>`).join('')}</select>
    </label>
    <label>Номер договора<input id="c-number" value="ДГ-2026-${String(Math.floor(Math.random() * 9000) + 1000)}"></label>
    <label>Направление подготовки<input id="c-program" value="02.03.03 Математическое обеспечение и администрирование информационных систем"></label>
    <div class="row">
      <label>Год приёма<input id="c-year" type="number" value="2026"></label>
      <label>Стоимость обучения, &#8381;<input id="c-total" type="number" step="0.01" value="480000.00"></label>
    </div>
    <label>Дата подписания<input id="c-signed" type="date" value="${new Date().toISOString().slice(0, 10)}"></label>
    <div class="error" id="c-error" hidden></div>
    <button id="c-save">Создать</button>`);

  el('c-save').onclick = () => submit('c-error', 'c-save', async () => {
    await api('/contracts', { method: 'POST', body: JSON.stringify({
      studentId: el('c-student').value,
      number: el('c-number').value,
      programName: el('c-program').value,
      admissionYear: Number(el('c-year').value),
      totalAmount: Number(el('c-total').value),
      signedOn: el('c-signed').value
    })});
  });
}

function issueInvoiceForm(contractId) {
  const today = new Date().toISOString().slice(0, 10);
  const due = new Date(Date.now() + 30 * 864e5).toISOString().slice(0, 10);

  showModal(`<h2>Начисление за период</h2>
    <label>Учебный период<input id="i-period" value="2026/2027-2" placeholder="2026/2027-1"></label>
    <label>Сумма, &#8381;<input id="i-amount" type="number" step="0.01" value="120000.00"></label>
    <div class="row">
      <label>Дата начисления<input id="i-issued" type="date" value="${today}"></label>
      <label>Срок оплаты<input id="i-due" type="date" value="${due}"></label>
    </div>
    <div class="error" id="i-error" hidden></div>
    <button id="i-save">Выставить</button>`);

  el('i-save').onclick = () => submit('i-error', 'i-save', async () => {
    await api(`/contracts/${contractId}/invoices`, { method: 'POST', body: JSON.stringify({
      periodCode: el('i-period').value,
      amount: Number(el('i-amount').value),
      issuedOn: el('i-issued').value,
      dueOn: el('i-due').value
    })});
  });
}

function splitForm(invoiceId) {
  const first = new Date(Date.now() + 14 * 864e5).toISOString().slice(0, 10);
  showModal(`<h2>Рассрочка</h2>
    <p class="muted">Сумма делится поровну, остаток копеек уходит в последнюю часть.</p>
    <div class="row">
      <label>Число частей<input id="s-parts" type="number" min="2" max="12" value="3"></label>
      <label>Интервал, дней<input id="s-interval" type="number" min="1" max="180" value="30"></label>
    </div>
    <label>Первый платёж<input id="s-first" type="date" value="${first}"></label>
    <div class="error" id="s-error" hidden></div>
    <button id="s-save">Оформить</button>`);

  el('s-save').onclick = () => submit('s-error', 's-save', async () => {
    await api(`/invoices/${invoiceId}/installments`, { method: 'POST', body: JSON.stringify({
      partCount: Number(el('s-parts').value),
      firstDueOn: el('s-first').value,
      intervalDays: Number(el('s-interval').value)
    })});
  });
}

async function renderPayments() {
  const [payments, refunds] = await Promise.all([api('/payments?take=100'), api('/refunds')]);

  el('content').innerHTML = `
    <div class="panel">
      <h2>Платежи</h2>
      <div class="sub">Возврат оформляется по успешному платежу на сумму не больше невозвращённого остатка.</div>
      ${payments.length ? `<table>
        <thead><tr><th>Дата</th><th>Плательщик</th><th>Договор</th><th class="num">Сумма</th><th>Статус</th><th>Операция шлюза</th><th></th></tr></thead>
        <tbody>${payments.map(p => `<tr>
          <td>${moment(p.createdAt)}</td>
          <td>${escape(p.studentName)}</td>
          <td class="muted">${escape(p.contractNumber)} &middot; ${escape(p.periodCode)}</td>
          <td class="num">${rub(p.amount)}${p.refundedAmount > 0 ? `<br><span class="muted" style="font-size:12px">&minus;${rub(p.refundedAmount)}</span>` : ''}</td>
          <td>${tag(paymentStatus, p.status)}</td>
          <td class="muted" style="font-size:12px">${escape(p.gatewayPaymentId || '—')}</td>
          <td class="num">${p.status === 'Succeeded' && p.refundedAmount < p.amount
            ? `<button class="ghost" data-refund="${p.id}" data-max="${p.amount - p.refundedAmount}">Возврат</button>` : ''}</td>
        </tr>`).join('')}</tbody></table>` : '<div class="empty">Платежей нет.</div>'}
    </div>
    <div class="panel">
      <h2>Возвраты</h2>
      ${refunds.length ? `<table>
        <thead><tr><th>Дата</th><th>Основание</th><th class="num">Сумма</th><th>Статус</th></tr></thead>
        <tbody>${refunds.map(r => `<tr>
          <td>${moment(r.createdAt)}</td>
          <td>${escape(r.reason)}</td>
          <td class="num">${rub(r.amount)}</td>
          <td>${tag(refundStatus, r.status)}</td>
        </tr>`).join('')}</tbody></table>` : '<div class="empty">Возвратов не было.</div>'}
    </div>`;

  document.querySelectorAll('[data-refund]').forEach(b => b.onclick = () => refundForm(b.dataset.refund, Number(b.dataset.max)));
}

function refundForm(paymentId, max) {
  showModal(`<h2>Возврат средств</h2>
    <p class="muted">Доступно к возврату: ${rub(max)}</p>
    <label>Сумма, &#8381;<input id="r-amount" type="number" step="0.01" min="0.01" max="${max}" value="${max.toFixed(2)}"></label>
    <label>Основание<input id="r-reason" value="Отчисление по собственному желанию"></label>
    <div class="error" id="r-error" hidden></div>
    <button id="r-save">Вернуть</button>`);

  el('r-save').onclick = () => submit('r-error', 'r-save', async () => {
    await api('/refunds', { method: 'POST', body: JSON.stringify({
      paymentId,
      amount: Number(el('r-amount').value),
      reason: el('r-reason').value
    })});
  });
}

async function renderLedger() {
  const [accounts, transactions] = await Promise.all([api('/ledger/accounts'), api('/ledger/transactions?take=40')]);

  el('content').innerHTML = `
    <div class="panel">
      <h2>Оборотная ведомость</h2>
      <div class="sub">Сальдо считается по проводкам, отдельного поля «баланс» в системе нет.</div>
      <table>
        <thead><tr><th>Счёт</th><th>Наименование</th><th class="num">Дебет</th><th class="num">Кредит</th><th class="num">Сальдо</th></tr></thead>
        <tbody>${accounts.map(a => `<tr>
          <td>${escape(a.code)}${a.contractNumber ? `<br><span class="muted" style="font-size:12px">${escape(a.contractNumber)}</span>` : ''}</td>
          <td>${escape(a.name)}</td>
          <td class="num">${rub(a.debitTotal)}</td>
          <td class="num">${rub(a.creditTotal)}</td>
          <td class="num"><b>${rub(a.balance)}</b></td>
        </tr>`).join('')}</tbody>
      </table>
    </div>
    <div class="panel">
      <h2>Журнал операций</h2>
      <div class="sub">Проводки только добавляются: ошибка исправляется сторнирующей операцией.</div>
      ${transactions.map(t => `<details>
        <summary>${moment(t.occurredAt)} &middot; ${escape(t.description)} &middot; ${rub(t.total)}</summary>
        <div class="entries">${t.entries.map(e =>
          `<div><span>${e.side === 'Debit' ? 'Дт' : 'Кт'} ${escape(e.accountCode)} ${escape(e.accountName)}</span><span>${rub(e.amount)}</span></div>`).join('')}</div>
      </details>`).join('') || '<div class="empty">Операций нет.</div>'}
    </div>`;
}

async function renderReconciliation() {
  const reports = await api('/reconciliation?take=14');

  el('page-actions').innerHTML = '<button id="run-recon">Сверить за сегодня</button>';
  el('run-recon').onclick = async () => {
    const button = el('run-recon');
    button.disabled = true;
    try {
      const report = await api('/reconciliation/run', { method: 'POST' });
      banner(`Сверка выполнена: сошлось ${report.matched}, расхождений ${report.discrepancies}.`,
        report.discrepancies ? 'warn' : '');
      await refresh();
    } catch (error) {
      banner(error.message, 'bad');
      button.disabled = false;
    }
  };

  el('content').innerHTML = `<div class="panel">
    <h2>Суточная сверка с реестром шлюза</h2>
    <div class="sub">Операции сопоставляются по идентификатору провайдера, а не по паре «сумма и дата».</div>
    ${reports.length ? reports.map(r => `<details ${r.discrepancies ? 'open' : ''}>
      <summary>${day(r.date)} &middot; у шлюза ${r.gatewayOperations}, у нас ${r.localPayments} &middot;
        сошлось ${r.matched} &middot; расхождений ${r.discrepancies}${r.autoRepaired ? `, исправлено ${r.autoRepaired}` : ''}</summary>
      <div class="entries">
        <div><span>Сумма успешных у шлюза</span><span>${rub(r.gatewayTotal)}</span></div>
        <div><span>Сумма успешных у нас</span><span>${rub(r.localTotal)}</span></div>
        <div><span>Расхождение журнала и витрины</span><span>${rub(r.ledgerDrift)}</span></div>
        ${r.issues.map(i => `<div><span>${escape(i.kind)}: ${escape(i.detail)}</span><span>${i.repaired ? 'исправлено' : ''}</span></div>`).join('')}
      </div>
    </details>`).join('') : '<div class="empty">Сверок ещё не было.</div>'}
  </div>`;
}

async function renderStudents() {
  const students = await api('/students');

  el('page-actions').innerHTML = '<button id="new-student">Завести обучающегося</button>';
  el('new-student').onclick = newStudentForm;

  el('content').innerHTML = `<div class="panel">
    <h2>Обучающиеся</h2>
    ${students.length ? `<table>
      <thead><tr><th>ФИО</th><th>Почта</th><th>Телефон</th></tr></thead>
      <tbody>${students.map(s => `<tr><td>${escape(s.fullName)}</td><td class="muted">${escape(s.email)}</td><td class="muted">${escape(s.phone || '—')}</td></tr>`).join('')}</tbody>
    </table>` : '<div class="empty">Пока никого нет.</div>'}
  </div>`;
}

function newStudentForm() {
  showModal(`<h2>Новый обучающийся</h2>
    <label>ФИО<input id="st-name" placeholder="Сидоров Пётр Иванович"></label>
    <label>Электронная почта<input id="st-email" type="email" placeholder="sidorov@synergy.local"></label>
    <label>Телефон<input id="st-phone" placeholder="+7 900 000-00-00"></label>
    <label>Временный пароль<input id="st-password" type="password" autocomplete="new-password"></label>
    <div class="error" id="st-error" hidden></div>
    <button id="st-save">Создать</button>`);

  el('st-save').onclick = () => submit('st-error', 'st-save', async () => {
    await api('/students', { method: 'POST', body: JSON.stringify({
      fullName: el('st-name').value,
      email: el('st-email').value,
      phone: el('st-phone').value || null,
      password: el('st-password').value
    })});
  });
}

/* ---------- модальное окно и вход ---------- */

function showModal(html) {
  el('modal-body').innerHTML = html;
  el('modal').hidden = false;
}

const closeModal = () => { el('modal').hidden = true; };

function submit(errorId, buttonId, action) {
  const button = el(buttonId);
  button.disabled = true;
  return action()
    .then(() => { closeModal(); return refresh(); })
    .catch(error => {
      const box = el(errorId);
      box.textContent = error.message;
      box.hidden = false;
      button.disabled = false;
    });
}

function logout() {
  banner(null);
  store.token = null;
  store.user = null;
  el('app').hidden = true;
  el('login').hidden = false;
}

function start() {
  el('login').hidden = true;
  el('app').hidden = false;
  el('user-name').textContent = store.user.fullName;
  el('user-role').textContent = role() === 'accountant' ? 'Бухгалтерия' : 'Обучающийся';
  buildNav();
  open(pages[role()][0].id);
}

el('login-hint').innerHTML =
  'Демонстрационные учётные записи: <b>buh@synergy.local</b> — бухгалтерия, '
  + '<b>ivanov@synergy.local</b> — обучающийся. Пароли задаются переменными окружения при первом запуске, '
  + 'см. README.';

el('login-form').onsubmit = async event => {
  event.preventDefault();
  const box = el('login-error');
  box.hidden = true;
  try {
    const result = await api('/auth/login', { method: 'POST', body: JSON.stringify({
      email: el('login-email').value,
      password: el('login-password').value
    })});
    store.token = result.token;
    store.user = { fullName: result.fullName, roles: result.roles };
    start();
  } catch (error) {
    box.textContent = error.message;
    box.hidden = false;
  }
};

el('logout').onclick = logout;
el('modal-close').onclick = closeModal;
el('modal').onclick = event => { if (event.target === el('modal')) closeModal(); };

// Шлюз возвращает плательщика сюда с результатом в адресе строки запроса.
// Сам статус к этому моменту уже перепроверен сервером обратным запросом.
const returned = new URLSearchParams(location.search).get('payment');
if (returned) history.replaceState(null, '', location.pathname);

if (store.token && store.user) {
  start();
  if (returned === 'succeeded') banner('Оплата прошла. Квитанция доступна в истории платежей.');
  else if (returned === 'canceled') banner('Оплата отклонена банком или отменена.', 'bad');
  else if (returned) banner('Платёж ещё обрабатывается, статус обновится автоматически.', 'warn');
}
