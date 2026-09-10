-- EF Core migration'ları çalıştırdıktan sonra (stock_items tablosu
-- oluşturulduktan sonra) uygulanmalıdır. Proje 1'deki demo verisiyle
-- aynı ürünler/miktarlar.
INSERT INTO stock_items (product_id, available_quantity) VALUES
    ('SKU-001', 100),
    ('SKU-002', 50),
    ('SKU-003', 10)
ON CONFLICT (product_id) DO NOTHING;
