const form = document.getElementById('profile-form');
const statusEl = document.getElementById('status');
const errorEl = document.getElementById('error');
const resultsSection = document.getElementById('results');
const amContainer = document.getElementById('am-steps');
const pmContainer = document.getElementById('pm-steps');

function getCheckedValues(group) {
  return Array.from(group.querySelectorAll('input[type="checkbox"]:checked')).map((i) => i.value);
}

function renderRoutine(routine) {
  amContainer.innerHTML = '';
  pmContainer.innerHTML = '';

  const makeCard = (step) => {
    const card = document.createElement('div');
    card.className = 'step-card';
    const header = document.createElement('header');
    header.innerHTML = `<span>Step ${step.stepNumber} • ${step.category}</span><span>${step.notes ? 'Usage' : ''}</span>`;
    const product = document.createElement('div');
    product.className = 'product';
    product.textContent = step.productName;
    const brand = document.createElement('div');
    brand.className = 'brand';
    brand.textContent = step.brand;
    const why = document.createElement('div');
    why.className = 'text';
    why.textContent = `Why: ${step.why}`;
    const notes = document.createElement('div');
    notes.className = 'text';
    notes.textContent = step.notes ? `Notes: ${step.notes}` : '';

    card.append(header, product, brand, why, notes);
    return card;
  };

  routine.am.forEach((step) => amContainer.appendChild(makeCard(step)));
  routine.pm.forEach((step) => pmContainer.appendChild(makeCard(step)));

  resultsSection.classList.remove('hidden');
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  errorEl.textContent = '';
  resultsSection.classList.add('hidden');
  statusEl.textContent = 'Generating your routine…';

  const skinType = document.getElementById('skinType').value;
  const concerns = getCheckedValues(document.querySelector('[data-name="concerns"]'));
  const sensitivities = getCheckedValues(document.querySelector('[data-name="sensitivities"]'));
  const toleratedActives = getCheckedValues(document.querySelector('[data-name="toleratedActives"]'));
  const maxSteps = parseInt(document.getElementById('maxSteps').value, 10);
  const budget = document.getElementById('budget').value;
  const country = document.getElementById('country').value.trim();

  const profile = {
    skinType,
    concerns,
    sensitivities,
    toleratedActives,
    maxSteps,
    budget,
    country
  };

  try {
    const res = await fetch('/api/recommend-routine', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ profile })
    });

    if (!res.ok) {
      const errorData = await res.json().catch(() => ({}));
      throw new Error(errorData.error || 'Something went wrong.');
    }

    const data = await res.json();
    renderRoutine(data.routine);
    statusEl.textContent = '';
  } catch (err) {
    console.error(err);
    statusEl.textContent = '';
    errorEl.textContent = err.message || 'Failed to generate routine. Please try again.';
  }
});
