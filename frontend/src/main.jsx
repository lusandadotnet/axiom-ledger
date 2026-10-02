import React, { useEffect, useMemo, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'

const WALLET_STORAGE_KEY = 'axiom-wallet'

function App() {
  const [wallet, setWallet] = useState(null)
  const [newWalletCurrency, setNewWalletCurrency] = useState('ZAR')
  const [amount, setAmount] = useState('100')
  const [history, setHistory] = useState({
    balance: 0,
    currency: 'ZAR',
    version: 0,
    transactions: [],
  })
  const [loading, setLoading] = useState(true)
  const [operation, setOperation] = useState('')
  const [message, setMessage] = useState('')
  const pendingKeys = useRef({ Deposit: null, Withdrawal: null })

  const formattedBalance = useMemo(
    () => new Intl.NumberFormat('en-ZA', {
      style: 'currency',
      currency: history.currency || wallet?.currency || 'ZAR',
    }).format(Number(history.balance || 0)),
    [history.balance, history.currency, wallet?.currency],
  )

  async function loadHistory(retries = 0) {
    if (!wallet) return

    const response = await fetch(
      `/api/history?walletId=${encodeURIComponent(wallet.walletId)}`,
      { headers: { 'X-Wallet-Token': wallet.accessToken } },
    )

    if (!response.ok) {
      if (response.status === 401) {
        throw new Error('Wallet access token is invalid. Create a new wallet.')
      }
      throw new Error('Could not load transaction history.')
    }

    setHistory(await response.json())
  }

  async function createWallet(currency) {
    setLoading(true)
    setMessage('Creating wallet…')

    try {
      const response = await fetch('/api/wallets', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ currency }),
      })

      const body = await response.json().catch(() => ({}))
      if (!response.ok) throw new Error(body.error || 'Could not create wallet.')

      const nextWallet = {
        walletId: body.walletId,
        accessToken: body.accessToken,
        currency: body.currency,
      }

      localStorage.setItem(WALLET_STORAGE_KEY, JSON.stringify(nextWallet))
      setWallet(nextWallet)
      setHistory({ balance: 0, currency: nextWallet.currency, version: 1, transactions: [] })
      setMessage('Wallet created. Add funds before making a withdrawal.')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not create wallet.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    const stored = localStorage.getItem(WALLET_STORAGE_KEY)

    if (stored) {
      try {
        const parsed = JSON.parse(stored)
        if (parsed.walletId && parsed.accessToken && parsed.currency) {
          setWallet(parsed)
          return
        }
      } catch {
        localStorage.removeItem(WALLET_STORAGE_KEY)
      }
    }

    createWallet('ZAR')
  }, [])

  useEffect(() => {
    if (!wallet) return

    setLoading(true)
    loadHistory()
      .catch((error) => setMessage(error instanceof Error ? error.message : 'Could not load wallet.'))
      .finally(() => setLoading(false))
  }, [wallet?.walletId])

  function resetPendingKeys() {
    pendingKeys.current = { Deposit: null, Withdrawal: null }
  }

  function updateAmount(value) {
    setAmount(value)
    resetPendingKeys()
  }

  async function executeTransaction(transactionType) {
    if (!wallet) return

    const numericAmount = Number(amount)
    if (!Number.isFinite(numericAmount) || numericAmount <= 0) {
      setMessage('Amount must be greater than zero.')
      return
    }

    const key = pendingKeys.current[transactionType] ?? crypto.randomUUID()
    pendingKeys.current[transactionType] = key
    setOperation(transactionType)
    setMessage('')

    try {
      const endpoint = transactionType === 'Deposit' ? '/api/deposit' : '/api/withdraw'
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Wallet-Token': wallet.accessToken,
          'Idempotency-Key': key,
        },
        body: JSON.stringify({
          walletId: wallet.walletId,
          amount: numericAmount,
          currency: wallet.currency,
        }),
      })

      const body = await response.json().catch(() => ({}))
      if (!response.ok) throw new Error(body.error || `${transactionType} failed.`)

      pendingKeys.current[transactionType] = null
      setMessage(`${transactionType} of ${numericAmount.toFixed(2)} ${wallet.currency} recorded successfully.`)

      // The write model is strongly consistent with Postgres; Redis is an
      // asynchronous read model, so give the projection a few short chances.
      for (let attempt = 0; attempt < 6; attempt += 1) {
        try {
          await loadHistory()
          break
        } catch (error) {
          if (attempt === 5) throw error
          await new Promise((resolve) => setTimeout(resolve, 400))
        }
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : `${transactionType} failed.`)
    } finally {
      setOperation('')
    }
  }

  async function copyToken() {
    if (!wallet?.accessToken) return
    try {
      await navigator.clipboard.writeText(wallet.accessToken)
      setMessage('Wallet access token copied.')
    } catch {
      setMessage('Could not copy the access token.')
    }
  }

  const canTransact = Boolean(wallet) && !loading && !operation

  return (
    <main className="page">
      <section className="hero">
        <div>
          <p className="eyebrow">AXIOM LEDGER</p>
          <h1>Wallet dashboard</h1>
          <p className="hero-copy">Event-sourced balances with a durable outbox and an ordered Redis read model.</p>
        </div>
        <div className="balance-card">
          <span>Current balance</span>
          <strong>{formattedBalance}</strong>
          <small>Ledger version {history.version}</small>
        </div>
      </section>

      <section className="grid">
        <section className="panel">
          <div className="panel-heading">
            <div>
              <h2>Wallet</h2>
              <p className="muted">Your wallet uses a private access token.</p>
            </div>
            <div className="new-wallet-controls">
              <select value={newWalletCurrency} onChange={(event) => setNewWalletCurrency(event.target.value)}>
                <option value="ZAR">ZAR</option>
                <option value="USD">USD</option>
                <option value="EUR">EUR</option>
                <option value="GBP">GBP</option>
              </select>
              <button className="secondary" onClick={() => createWallet(newWalletCurrency)} disabled={loading}>
                New wallet
              </button>
            </div>
          </div>

          {wallet ? (
            <div className="credentials">
              <label>
                Wallet ID
                <input value={wallet.walletId} readOnly />
              </label>
              <label>
                Wallet currency
                <input value={wallet.currency} readOnly />
              </label>
              <label>
                Access token
                <div className="token-row">
                  <input value={wallet.accessToken} readOnly />
                  <button type="button" className="secondary" onClick={copyToken}>Copy</button>
                </div>
              </label>
            </div>
          ) : (
            <p className="muted">Initialising wallet…</p>
          )}
        </section>

        <section className="panel">
          <h2>Move money</h2>
          <label>
            Amount
            <input
              type="number"
              min="0.01"
              step="0.01"
              value={amount}
              onChange={(event) => updateAmount(event.target.value)}
            />
          </label>
          <p className="muted">Currency: {wallet?.currency || '—'}</p>
          <div className="action-row">
            <button disabled={!canTransact} onClick={() => executeTransaction('Deposit')}>
              {operation === 'Deposit' ? 'Depositing…' : 'Add funds'}
            </button>
            <button disabled={!canTransact} onClick={() => executeTransaction('Withdrawal')}>
              {operation === 'Withdrawal' ? 'Withdrawing…' : 'Withdraw'}
            </button>
          </div>
          {message && <p className="message">{message}</p>}
        </section>

        <section className="panel wide">
          <div className="panel-heading">
            <div>
              <h2>Recent transactions</h2>
              <p className="muted">Projected from the Postgres event stream into Redis.</p>
            </div>
            <button className="secondary" onClick={() => loadHistory().catch((error) => setMessage(error.message))} disabled={!wallet || loading}>
              Refresh
            </button>
          </div>

          {history.transactions.length === 0 ? (
            <p className="muted">No transactions yet. Add funds to create the first ledger entry.</p>
          ) : (
            <div className="transactions">
              {history.transactions.map((transaction) => {
                const isDeposit = transaction.type === 'Deposit'
                const amountPrefix = isDeposit ? '+' : '−'

                return (
                  <article key={transaction.transactionId}>
                    <div>
                      <strong>{transaction.type}</strong>
                      <small>
                        v{transaction.ledgerVersion} · {new Date(transaction.occurredAt).toLocaleString()}
                      </small>
                    </div>
                    <span className={isDeposit ? 'positive' : 'negative'}>
                      {amountPrefix}{Number(transaction.amount).toFixed(2)} {transaction.currency}
                    </span>
                  </article>
                )
              })}
            </div>
          )}
        </section>
      </section>
    </main>
  )
}

createRoot(document.getElementById('root')).render(<App />)
