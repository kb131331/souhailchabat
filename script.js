document.getElementById('contact-form').addEventListener('submit', function(e) {
    e.preventDefault();
    alert('Thanks for reaching out! We will contact you soon.');
    this.reset();
});

// Smooth scrolling for internal links
document.querySelectorAll('a[href^="#"]').forEach(link => {
    link.addEventListener('click', function(e) {
        const target = document.querySelector(this.getAttribute('href'));
        if (target) {
            e.preventDefault();
            target.scrollIntoView({ behavior: 'smooth' });
        }
    });
});

// Fetch trading history from cTrader Open API
async function loadHistory() {
    // Replace with your real access token and account ID
    const token = '';
    const accountId = '';
    if (!token || !accountId) {
        console.warn('cTrader credentials missing');
        return;
    }
    const tbody = document.querySelector('#history-table tbody');
    try {
        const res = await fetch(`https://openapi.spotware.com/connect/trading-accounts/${accountId}/history`, {
            headers: {
                'Authorization': `Bearer ${token}`
            }
        });
        const data = await res.json();
        tbody.innerHTML = '';
        (data.trades || data).forEach(trade => {
            const row = document.createElement('tr');
            row.innerHTML = `<td>${trade.closeTime}</td><td>${trade.symbolName}</td><td>${trade.volume}</td><td>${trade.profit}</td>`;
            tbody.appendChild(row);
        });
    } catch (err) {
        console.error(err);
        tbody.innerHTML = '<tr><td colspan="4">Failed to load history</td></tr>';
    }
}

document.addEventListener('DOMContentLoaded', loadHistory);
