import React, { useEffect, useMemo, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

const defaultWallet = '11111111-1111-1111-1111-111111111111'

function App() {
  const [walletId, setWalletId] = useState(localStorage.getItem('axiom-wallet-id') || defaultWallet)
  const [amount, setAmount] = useState('100')
  const [currency, setCurrency] = useState('ZAR')
  const [history, setHistory] = useState({ balance: 0, currency: 'ZAR', transactions: [] })
  const [loading, setLoading] = useState(false)
  const [message, setMessage] = useState('')

  const formattedBalance = useMemo(
    () => new Intl.NumberFormat('en-ZA', { style: 'currency', currency: history.currency || currency }).format(history.balance || 0),
    [history.balance, history.currency, currency],
  )

  async function loadHistory() {
    const response = await fetch(`/api/history?walletId=${encodeURIComponent(walletId)}`)
    if (!response.ok) throw new Error('Could not load transaction history.')
    setHistory(await response.json())
  }

  useEffect(() => {
    localStorage.setItem('axiom-wallet-id', walletId)
    loadHistory().catch(() => setMessage('Start the Docker stack, then refresh this page.'))
  }, [walletId])

  async function withdraw(event) {
    event.preventDefault()
    setLoading(true)
    setMessage('')
    try {
      const response = await fetch('/api/withdraw', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ walletId, amount: Number(amount), currency }),
      })
      const body = await response.json().catch(() => ({}))
      if (!response.ok) throw new Error(body.error || 'Withdrawal failed.')
      setMessage(`Withdrawal ${body.transactionId} recorded successfully.`)
      await loadHistory()
    } catch (error) {
      setMessage(error.message)
    } finally {
      setLoading(false)
    }
  }

  return (
    <main className="page">
      <section className="hero">
        <div>
          <p className="eyebrow">AXIOM LEDGER</p>
          <h1>Wallet dashboard</h1>
        </div>
        <div className="balance-card">
          <span>Current balance</span>
          <strong>{formattedBalance}</strong>
        </div>
      </section>

      <section className="grid">
        <form className="panel" onSubmit={withdraw}>
          <h2>Make a withdrawal</h2>
          <label>
            Wallet ID
            <input value={walletId} onChange={(e) => setWalletId(e.target.value)} />
          </label>
          <label>
            Amount
            <input type="number" min="0.01" step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </label>
          <label>
            Currency
            <input maxLength="3" value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} />
          </label>
          <button disabled={loading}>{loading ? 'Processing…' : 'Withdraw'}</button>
          {message && <p className="message">{message}</p>}
        </form>

        <section className="panel">
          <div className="panel-heading">
            <div>
              <h2>Recent transactions</h2>
              <p className="muted">Read directly from Redis</p>
            </div>
            <button className="secondary" onClick={() => loadHistory().catch(() => setMessage('Refresh failed.'))}>Refresh</button>
          </div>
          {history.transactions.length === 0 ? (
            <p className="muted">No transactions yet.</p>
          ) : (
            <div className="transactions">
              {history.transactions.map((transaction) => (
                <article key={transaction.transactionId}>
                  <div>
                    <strong>{transaction.type}</strong>
                    <small>{new Date(transaction.occurredAt).toLocaleString()}</small>
                  </div>
                  <span>−{transaction.amount.toFixed(2)} {transaction.currency}</span>
                </article>
              ))}
            </div>
          )}
        </section>
      </section>
    </main>
  )
}

createRoot(document.getElementById('root')).render(<App />)
