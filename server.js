const express = require('express');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;

app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

function buildMockRoutine(profile) {
  const {
    skinType = '',
    concerns = [],
    sensitivities = [],
    toleratedActives = [],
    maxSteps = 4,
    budget = 'medium',
    country = ''
  } = profile || {};

  const isOily = /oily/i.test(skinType);
  const hasAcneConcerns = concerns.some((c) => /acne|clogged pores/i.test(c));
  const fragranceFree = sensitivities.some((s) => /fragrance/i.test(s));

  const stepLimit = Math.max(3, Math.min(Number(maxSteps) || 4, 5));

  const cleanser = isOily
    ? {
        category: 'Cleanser',
        productName: 'Gentle Foaming Gel Cleanser',
        brand: fragranceFree ? 'La Roche-Posay (fragrance-free)' : 'CeraVe',
        why: 'Foam/gel texture removes excess sebum without stripping oily skin.',
        notes: 'Massage 30-60 seconds, lukewarm water.'
      }
    : {
        category: 'Cleanser',
        productName: 'Cream Cleanser',
        brand: fragranceFree ? 'Vanicream (fragrance-free)' : 'CeraVe',
        why: 'Cream texture keeps barrier comfortable for non-oily skin.',
        notes: 'Massage 30-60 seconds, lukewarm water.'
      };

  const moisturizer = isOily
    ? {
        category: 'Moisturizer',
        productName: 'Oil-Free Gel-Cream Moisturizer',
        brand: fragranceFree ? 'Neutrogena Hydro Boost Fragrance-Free' : 'La Roche-Posay',
        why: 'Light gel texture hydrates without heaviness, good for oily skin.',
        notes: 'Apply pea to nickel size over damp skin.'
      }
    : {
        category: 'Moisturizer',
        productName: 'Barrier Repair Cream',
        brand: fragranceFree ? 'Vanicream' : 'CeraVe',
        why: 'Ceramides and occlusives support the moisture barrier.',
        notes: 'Apply after treatments, lock in hydration.'
      };

  const sunscreen = {
    category: 'Sunscreen',
    productName: isOily ? 'Matte SPF 50 Gel Sunscreen' : 'Hydrating SPF 50 Lotion',
    brand: fragranceFree ? 'Eucerin Sensitive Protect' : 'La Roche-Posay Anthelios',
    why: 'Daily broad-spectrum SPF to protect against UV and post-acne marks.',
    notes: '2 finger-lengths for face/neck, reapply if outdoors.'
  };

  const saTreatment = {
    category: 'Exfoliant',
    productName: '2% Salicylic Acid Leave-On',
    brand: fragranceFree ? 'Paula’s Choice BHA (fragrance-free)' : 'The Ordinary',
    why: 'Unclogs pores and smooths texture for acne/clogging concerns.',
    notes: 'Use after cleansing, avoid eye area; start 2-3x/week.'
  };

  const bpTreatment = {
    category: 'Acne Treatment',
    productName: '2.5% Benzoyl Peroxide Gel',
    brand: fragranceFree ? 'Paula’s Choice CLEAR (fragrance-free)' : 'La Roche-Posay Effaclar',
    why: 'Targets acne-causing bacteria and reduces inflamed lesions.',
    notes: 'Thin layer on breakout-prone areas; can be short-contact (2-5 mins) for sensitive skin.'
  };

  const retinoid = {
    category: 'Retinoid',
    productName: 'Adapalene 0.1% Gel',
    brand: fragranceFree ? 'Differin (fragrance-free)' : 'Differin',
    why: 'Supports acne control and skin renewal; helps fade dark marks.',
    notes: 'Pea-sized amount over dry skin at night; moisturize after to reduce irritation.'
  };

  const amSteps = [];
  const pmSteps = [];

  amSteps.push(cleanser);
  if (hasAcneConcerns && amSteps.length < stepLimit) {
    amSteps.push({
      category: saTreatment.category,
      productName: saTreatment.productName,
      brand: saTreatment.brand,
      why: saTreatment.why,
      notes: saTreatment.notes
    });
  }
  if (amSteps.length < stepLimit) {
    amSteps.push({
      category: moisturizer.category,
      productName: moisturizer.productName,
      brand: moisturizer.brand,
      why: moisturizer.why,
      notes: moisturizer.notes
    });
  }
  if (amSteps.length < stepLimit) {
    amSteps.push(sunscreen);
  }

  pmSteps.push(cleanser);
  if (hasAcneConcerns && pmSteps.length < stepLimit) {
    pmSteps.push(bpTreatment);
  }
  if (hasAcneConcerns && pmSteps.length < stepLimit && toleratedActives.some((a) => /retinoid|retinol|adapalene/i.test(a))) {
    pmSteps.push(retinoid);
  } else if (pmSteps.length < stepLimit && !hasAcneConcerns) {
    pmSteps.push({
      category: 'Treatment',
      productName: 'Niacinamide 5% Serum',
      brand: fragranceFree ? 'The Ordinary (fragrance-free)' : 'Good Molecules',
      why: 'Balances oil and supports barrier; gentle for most skin types.',
      notes: 'Apply after cleansing on dry skin.'
    });
  }
  if (pmSteps.length < stepLimit) {
    pmSteps.push(moisturizer);
  }

  const applyStepNumbers = (steps) =>
    steps.map((step, index) => ({ ...step, stepNumber: index + 1 }));

  return {
    am: applyStepNumbers(amSteps),
    pm: applyStepNumbers(pmSteps),
    meta: {
      budget,
      country,
      note: 'Mock routine generated locally; final version will personalize further with live product data.'
    }
  };
}

app.post('/api/recommend-routine', (req, res) => {
  const { profile } = req.body || {};
  if (!profile) {
    return res.status(400).json({ error: 'Profile is required.' });
  }

  const routine = buildMockRoutine(profile);
  res.json({ routine });
});

app.listen(PORT, () => {
  console.log(`SkinRoutine AI server running on http://localhost:${PORT}`);
});

module.exports = { app, buildMockRoutine };
